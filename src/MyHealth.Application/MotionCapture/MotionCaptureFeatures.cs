using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Media;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.MotionCapture;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.MotionCapture;

namespace MyHealth.Application.MotionCapture;

/// <summary>Limits of the 3D conversion, set by the host (<c>MotionCapture:*</c>).</summary>
public sealed record MotionCaptureSettings(long MaxUploadBytes, int MonthlyJobsPerTenant, bool DeleteSourceAfterProcessing, TimeSpan PollInterval);

/// <summary>What the trainer accepts before sending a video (LGPD: the video shows a person and goes to a third party).</summary>
public static class MotionCaptureTerms
{
    public const string Text = """
        O vídeo será enviado ao serviço de captura de movimento (DeepMotion) apenas para gerar o modelo 3D deste exercício.
        Grave só você mesmo, ou alguém que autorizou por escrito — nunca clientes. O rosto não é usado no modelo 3D.
        O vídeo original é apagado do MyHealth quando a conversão termina; o provedor não pode usá-lo para outros fins.
        """;
}

public sealed record MotionCaptureTermsResponse(int Version, string Text);

public sealed record MotionCaptureOutputResponse(MediaKind Kind, CameraAngle? Angle, string Format, string Url, long SizeBytes);

public sealed record MotionCaptureJobResponse(
    Guid Id, Guid ExerciseId, MotionCaptureStatus Status, int ProgressPercent, string? Error, int DurationSeconds, string SourceFileName,
    IReadOnlyList<MotionCaptureOutputResponse> Outputs, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? AcceptedAt, bool SourceDeleted);

public sealed record GetMotionCaptureTermsQuery : IQuery<MotionCaptureTermsResponse>;

internal sealed class GetMotionCaptureTermsHandler : IQueryHandler<GetMotionCaptureTermsQuery, MotionCaptureTermsResponse>
{
    public Task<Result<MotionCaptureTermsResponse>> Handle(GetMotionCaptureTermsQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<MotionCaptureTermsResponse>>(new MotionCaptureTermsResponse(MotionCaptureJob.CurrentConsentVersion, MotionCaptureTerms.Text));
}

/// <summary>
/// The trainer who created an exercise sends a video of the movement to be turned into a 3D model. The video is
/// stored privately and the background worker sends it to the provider; this request returns at once.
/// </summary>
public sealed record StartMotionCaptureCommand(
    Guid ExerciseId, string FileName, string ContentType, long SizeBytes, int DurationSeconds, int ConsentVersion, Stream Content)
    : ICommand<MotionCaptureJobResponse>;

internal sealed class StartMotionCaptureValidator : AbstractValidator<StartMotionCaptureCommand>
{
    public StartMotionCaptureValidator()
    {
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.ContentType).NotEmpty().MaximumLength(100);
    }
}

internal sealed class StartMotionCaptureHandler(
    ICareAccess access, IExerciseRepository exercises, IMotionCaptureJobRepository jobs, IMediaStorage storage, IMotionCaptureProvider provider,
    MotionCaptureSettings settings, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<StartMotionCaptureCommand, MotionCaptureJobResponse>
{
    public async Task<Result<MotionCaptureJobResponse>> Handle(StartMotionCaptureCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var exercise = await exercises.GetByIdAsync(command.ExerciseId, cancellationToken);
        if (exercise is null || exercise.IsArchived || !exercise.IsAvailableIn(trainer.Value.TenantId))
        {
            return ExerciseErrors.NotFound;
        }

        if (!exercise.CanBeEditedBy(trainer.Value.Profile.Id))
        {
            return ExerciseErrors.NotOwner;
        }

        if (command.SizeBytes > settings.MaxUploadBytes)
        {
            return MotionCaptureErrors.TooLarge;
        }

        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        if (await jobs.CountForTenantSinceAsync(trainer.Value.TenantId, monthStart, cancellationToken) >= settings.MonthlyJobsPerTenant)
        {
            return MotionCaptureErrors.MonthlyLimitReached;
        }

        var id = Guid.CreateVersion7(now);
        var extension = Path.GetExtension(command.FileName).ToLowerInvariant() switch
        {
            ".mov" => ".mov",
            ".webm" => ".webm",
            ".m4v" => ".m4v",
            _ => ".mp4",
        };
        var key = $"{IMediaStorage.PrivatePrefix}motion-sources/{trainer.Value.TenantId:N}/{id:N}{extension}";
        var job = MotionCaptureJob.Create(
            id, exercise, trainer.Value.TenantId, trainer.Value.Profile.Id, trainer.Value.Profile.UserId, provider.Name, key, Path.GetFileName(command.FileName),
            command.ContentType, command.SizeBytes, command.DurationSeconds, command.ConsentVersion, now);
        if (job.IsFailure)
        {
            return job.Error;
        }

        await storage.SaveAsync(key, command.Content, command.ContentType, cancellationToken);
        jobs.Add(job.Value);
        audit.Record(AuditActions.Create, nameof(MotionCaptureJob), id, subjectUserId: null, trainer.Value.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return MotionCaptureMapping.ToResponse(job.Value);
    }
}

public sealed record ListMotionCapturesQuery(Guid ExerciseId) : IQuery<IReadOnlyList<MotionCaptureJobResponse>>;

internal sealed class ListMotionCapturesHandler(IReadDbContext db, ICareAccess access) : IQueryHandler<ListMotionCapturesQuery, IReadOnlyList<MotionCaptureJobResponse>>
{
    public async Task<Result<IReadOnlyList<MotionCaptureJobResponse>>> Handle(ListMotionCapturesQuery query, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var jobs = await db.MotionCaptureJobs
            .Where(j => j.ExerciseId == query.ExerciseId && j.TenantId == trainer.Value.TenantId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
        return jobs.Select(MotionCaptureMapping.ToResponse).ToList();
    }
}

/// <summary>
/// The trainer puts the result on the exercise: the 3D model, and the rendered videos as loops (one per camera
/// angle, replacing earlier loops of the same angle). The exercise needs its text description first (accessibility).
/// </summary>
public sealed record AcceptMotionCaptureCommand(Guid JobId) : ICommand<MotionCaptureJobResponse>;

internal sealed class AcceptMotionCaptureHandler(
    ICareAccess access, IMotionCaptureJobRepository jobs, IExerciseRepository exercises, IMediaHostPolicy hosts, IAuditLog audit, IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<AcceptMotionCaptureCommand, MotionCaptureJobResponse>
{
    public async Task<Result<MotionCaptureJobResponse>> Handle(AcceptMotionCaptureCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var job = await jobs.GetByIdAsync(command.JobId, cancellationToken);
        if (job is null || job.TenantId != trainer.Value.TenantId)
        {
            if (job is not null)
            {
                await audit.RecordDeniedAsync(nameof(MotionCaptureJob), job.Id, trainer.Value.TenantId, cancellationToken);
            }

            return MotionCaptureErrors.NotFound;
        }

        var exercise = await exercises.GetByIdAsync(job.ExerciseId, cancellationToken);
        if (exercise is null || !exercise.IsAvailableIn(trainer.Value.TenantId))
        {
            return ExerciseErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        var accepted = job.Accept(now);
        if (accepted.IsFailure)
        {
            return accepted.Error;
        }

        var added = job.Outputs
            .Where(o => (o.Kind == MediaKind.Loop && o.Angle is not null) || (o.Kind == MediaKind.Model3D && o.Format == "glb"))
            .Where(o => hosts.IsAllowed(o.Url))
            .Select(o => new ExerciseMedia(Guid.CreateVersion7(now), o.Kind, o.Angle, o.Url, null, o.Kind == MediaKind.Loop ? job.DurationSeconds : null, null, []))
            .ToList();
        if (added.Count == 0)
        {
            return MotionCaptureErrors.NotReady;
        }

        var replacedAngles = added.Where(m => m.Kind == MediaKind.Loop).Select(m => m.Angle).ToHashSet();
        var kept = exercise.Media.Where(m => !(m.Kind == MediaKind.Loop && replacedAngles.Contains(m.Angle)) && m.Kind != MediaKind.Model3D);
        var result = exercise.SetMedia(trainer.Value.Profile.Id, [.. kept, .. added], now);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record(AuditActions.Update, nameof(Exercise), exercise.Id, subjectUserId: null, trainer.Value.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return MotionCaptureMapping.ToResponse(job);
    }
}

internal static class MotionCaptureMapping
{
    public static MotionCaptureJobResponse ToResponse(MotionCaptureJob job) =>
        new(job.Id, job.ExerciseId, job.Status, job.ProgressPercent, job.Error, job.DurationSeconds, job.SourceFileName,
            [.. job.Outputs.Select(o => new MotionCaptureOutputResponse(o.Kind, o.Angle, o.Format, o.Url, o.SizeBytes))],
            job.CreatedAt, job.CompletedAt, job.AcceptedAt, job.SourceDeletedAt is not null);
}
