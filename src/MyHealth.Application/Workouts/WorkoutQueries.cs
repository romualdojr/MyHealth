using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Common;
using MyHealth.Application.Users;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.Workouts;

public sealed record SetLogResponse(
    Guid Id, Guid ExerciseId, Guid? PlannedExerciseId, int SetNumber, int? Repetitions, decimal? LoadKg, int? DurationSeconds,
    decimal? DistanceMeters, int? Difficulty, string? Notes, DateTimeOffset LoggedAt, IReadOnlyList<SetLogCorrection> Corrections);

public sealed record WorkoutSessionResponse(
    Guid Id,
    Guid TenantId,
    Guid ClientId,
    Guid TrainingPlanId,
    int PlanVersionNumber,
    Guid PlannedSessionId,
    string PlannedSessionName,
    SessionKind Kind,
    WorkoutStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? AbandonedAt,
    DateOnly? LocalDate,
    int? ActiveDurationSeconds,
    IReadOnlyList<SetLogResponse> Sets,
    WorkoutFeedback? Feedback,
    FeedbackReply? Reply,
    ExternalWorkoutExport? AppleHealthWorkout);

/// <summary>
/// Readable by the client, or by the responsible trainer while acting for the session's tenant with an active
/// relationship there. Anyone else gets "not found", and the attempt is audited (MT-09).
/// </summary>
public sealed record GetWorkoutSessionQuery(Guid SessionId) : IQuery<WorkoutSessionResponse>;

internal sealed class GetWorkoutSessionHandler(IReadDbContext db, IUserContext user, ITenantContext tenant, IAuditLog audit)
    : IQueryHandler<GetWorkoutSessionQuery, WorkoutSessionResponse>
{
    public async Task<Result<WorkoutSessionResponse>> Handle(GetWorkoutSessionQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var session = await db.WorkoutSessions
            .Include(s => s.Sets)
            .Include(s => s.Pauses)
            .FirstOrDefaultAsync(s => s.Id == query.SessionId, cancellationToken);

        if (session is null)
        {
            return WorkoutErrors.NotFound;
        }

        if (session.ClientId == user.UserId)
        {
            return WorkoutMapping.ToResponse(session);
        }

        var activeTenantId = membership.Value?.TenantId;
        var allowed = session.TrainerUserId == user.UserId
                      && session.TenantId == activeTenantId
                      && membership.Value!.Practices(Discipline.PersonalTrainer)
                      && await db.CareRelationships.AnyAsync(
                          r => r.TenantId == session.TenantId && r.ProfessionalProfileId == session.TrainerProfileId
                               && r.ClientId == session.ClientId && r.Status == RelationshipStatus.Active,
                          cancellationToken);

        if (!allowed)
        {
            await audit.RecordDeniedAsync(nameof(WorkoutSession), session.Id, activeTenantId, cancellationToken);
            return WorkoutErrors.NotFound;
        }

        return WorkoutMapping.ToResponse(session);
    }
}

public sealed record WorkoutSessionListItem(
    Guid Id, Guid TenantId, Guid TrainingPlanId, int PlanVersionNumber, string PlannedSessionName, SessionKind Kind, WorkoutStatus Status,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, DateOnly? LocalDate, int? PerceivedExertion, bool ReportedDiscomfort, bool HasReply);

public sealed record ListMyWorkoutSessionsQuery(DateTimeOffset? From, DateTimeOffset? To) : IQuery<IReadOnlyList<WorkoutSessionListItem>>;

internal sealed class ListMyWorkoutSessionsHandler(IReadDbContext db, IUserContext user)
    : IQueryHandler<ListMyWorkoutSessionsQuery, IReadOnlyList<WorkoutSessionListItem>>
{
    public async Task<Result<IReadOnlyList<WorkoutSessionListItem>>> Handle(ListMyWorkoutSessionsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await WorkoutMapping.ListAsync(db.WorkoutSessions.Where(s => s.ClientId == user.UserId), query.From, query.To, cancellationToken));
}

/// <summary>Attendance between two local dates (inclusive). Only completed sessions count.</summary>
public sealed record GetAttendanceQuery(DateOnly From, DateOnly To) : IQuery<WeeklyAttendance>;

internal sealed class GetAttendanceValidator : AbstractValidator<GetAttendanceQuery>
{
    public GetAttendanceValidator() =>
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From)
            .Must((q, to) => to.DayNumber - q.From.DayNumber <= 366).WithMessage("The range cannot exceed one year.");
}

internal sealed class GetAttendanceHandler(IReadDbContext db, IUserContext user) : IQueryHandler<GetAttendanceQuery, WeeklyAttendance>
{
    public async Task<Result<WeeklyAttendance>> Handle(GetAttendanceQuery query, CancellationToken cancellationToken) =>
        await AttendanceCalculator.CalculateAsync(db, user.UserId, query.From, query.To, cancellationToken);
}

/// <summary>
/// Per-session figures for one exercise. Load-based and duration-based measures are reported separately and are
/// null when not recorded — nothing is estimated (RF-20).
/// </summary>
public sealed record ExerciseProgressPoint(
    Guid SessionId,
    DateOnly Date,
    int Sets,
    decimal? MaxLoadKg,
    int? TotalRepetitions,
    decimal? TotalVolumeKg,
    int? TotalDurationSeconds,
    decimal? TotalDistanceMeters,
    string Source);

public sealed record PersonalBest(decimal LoadKg, int Repetitions, DateOnly Date, Guid SessionId);

public sealed record ExerciseProgressResponse(Guid ExerciseId, IReadOnlyList<ExerciseProgressPoint> Points, PersonalBest? HeaviestSet);

public sealed record GetExerciseProgressQuery(Guid ExerciseId, DateOnly? From, DateOnly? To) : IQuery<ExerciseProgressResponse>;

internal sealed class GetExerciseProgressHandler(IReadDbContext db, IUserContext user)
    : IQueryHandler<GetExerciseProgressQuery, ExerciseProgressResponse>
{
    public const string SelfReportedSource = "self-reported";

    public async Task<Result<ExerciseProgressResponse>> Handle(GetExerciseProgressQuery query, CancellationToken cancellationToken)
    {
        var sessions = db.WorkoutSessions.Where(s => s.ClientId == user.UserId && s.Status == WorkoutStatus.Completed);
        if (query.From is { } from)
        {
            sessions = sessions.Where(s => s.LocalDate >= from);
        }

        if (query.To is { } to)
        {
            sessions = sessions.Where(s => s.LocalDate <= to);
        }

        var rows = await sessions
            .Where(s => s.Sets.Any(set => set.ExerciseId == query.ExerciseId))
            .OrderBy(s => s.CompletedAt)
            .Select(s => new { s.Id, LocalDate = s.LocalDate!.Value, Sets = s.Sets.Where(set => set.ExerciseId == query.ExerciseId).ToList() })
            .ToListAsync(cancellationToken);

        var points = rows.Select(row => new ExerciseProgressPoint(
            row.Id,
            row.LocalDate,
            row.Sets.Count,
            row.Sets.Max(s => s.LoadKg),
            SumOrNull(row.Sets.Select(s => s.Repetitions)),
            SumOrNull(row.Sets.Select(s => s.Volume)),
            SumOrNull(row.Sets.Select(s => s.DurationSeconds)),
            SumOrNull(row.Sets.Select(s => s.DistanceMeters)),
            SelfReportedSource)).ToList();

        var heaviest = rows
            .SelectMany(row => row.Sets
                .Where(s => s.LoadKg is not null && s.Repetitions is > 0)
                .Select(s => new PersonalBest(s.LoadKg!.Value, s.Repetitions!.Value, row.LocalDate, row.Id)))
            .OrderByDescending(p => p.LoadKg)
            .ThenByDescending(p => p.Repetitions)
            .ThenBy(p => p.Date)
            .FirstOrDefault();

        return new ExerciseProgressResponse(query.ExerciseId, points, heaviest);
    }

    private static int? SumOrNull(IEnumerable<int?> values)
    {
        var known = values.Where(v => v is not null).ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    private static decimal? SumOrNull(IEnumerable<decimal?> values)
    {
        var known = values.Where(v => v is not null).ToList();
        return known.Count == 0 ? null : known.Sum();
    }
}

public sealed record TrainerClientSummary(
    Guid ClientId,
    string ClientName,
    Guid RelationshipId,
    string? ActivePlanName,
    int? SuggestedSessionsPerWeek,
    int CompletedLast7Days,
    double? AdherenceLast7Days,
    int PendingFeedbackReplies,
    int UnrepliedDiscomfortReports,
    DateTimeOffset? LastCompletedAt,
    int PlansNeedingReview);

public sealed record TrainerDashboardResponse(
    IReadOnlyList<TrainerClientSummary> Clients, int TotalPendingFeedback, int TotalDiscomfortReports, int TotalPlansNeedingReview);

/// <summary>
/// Planned-vs-actual adherence, pending feedback, reported discomfort and plans flagged for review (RF-21, RF-23) for the
/// trainer's active clients.
/// </summary>
public sealed record GetTrainerDashboardQuery : IQuery<TrainerDashboardResponse>;

internal sealed class GetTrainerDashboardHandler(IReadDbContext db, ICareAccess access, TimeProvider clock)
    : IQueryHandler<GetTrainerDashboardQuery, TrainerDashboardResponse>
{
    public async Task<Result<TrainerDashboardResponse>> Handle(GetTrainerDashboardQuery query, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var trainerId = trainer.Value.Profile.Id;
        var tenantId = trainer.Value.TenantId;
        var since = clock.GetUtcNow().AddDays(-7);

        var clients = await (
                from r in db.CareRelationships
                join c in db.UserProfiles on r.ClientId equals c.Id
                where r.TenantId == tenantId && r.ProfessionalProfileId == trainerId && r.Status == RelationshipStatus.Active
                select new { ClientId = c.Id, c.FullName, RelationshipId = r.Id })
            .ToListAsync(cancellationToken);

        var clientIds = clients.Select(c => c.ClientId).ToList();

        var activePlans = await db.TrainingPlans
            .Where(p => p.TenantId == tenantId && p.TrainerProfileId == trainerId && clientIds.Contains(p.ClientId) && p.Status == PlanStatus.Published)
            .Select(p => new { p.ClientId, p.Details.Name, p.Details.SuggestedSessionsPerWeek, p.UpdatedAt })
            .ToListAsync(cancellationToken);

        var flagged = await db.TrainingPlans
            .Where(p => p.TenantId == tenantId && p.TrainerProfileId == trainerId && clientIds.Contains(p.ClientId) && p.ReviewRequestedAt != null)
            .Select(p => p.ClientId)
            .ToListAsync(cancellationToken);

        var completed = await db.WorkoutSessions
            .Where(s => s.TenantId == tenantId && s.TrainerProfileId == trainerId && clientIds.Contains(s.ClientId) && s.Status == WorkoutStatus.Completed)
            .Select(s => new { s.ClientId, s.CompletedAt, HasReply = s.Reply != null, Discomfort = s.Feedback!.ReportedDiscomfort })
            .ToListAsync(cancellationToken);

        var summaries = clients.Select(client =>
        {
            var plan = activePlans.Where(p => p.ClientId == client.ClientId).MaxBy(p => p.UpdatedAt);
            var mine = completed.Where(s => s.ClientId == client.ClientId).ToList();
            var recent = mine.Count(s => s.CompletedAt >= since);
            return new TrainerClientSummary(
                client.ClientId,
                client.FullName,
                client.RelationshipId,
                plan?.Name,
                plan?.SuggestedSessionsPerWeek,
                recent,
                plan is null ? null : Math.Round((double)recent / plan.SuggestedSessionsPerWeek, 2),
                mine.Count(s => !s.HasReply),
                mine.Count(s => s.Discomfort && !s.HasReply),
                mine.Max(s => s.CompletedAt),
                flagged.Count(id => id == client.ClientId));
        }).OrderByDescending(s => s.UnrepliedDiscomfortReports).ThenBy(s => s.ClientName).ToList();

        return new TrainerDashboardResponse(
            summaries, summaries.Sum(s => s.PendingFeedbackReplies), summaries.Sum(s => s.UnrepliedDiscomfortReports), summaries.Sum(s => s.PlansNeedingReview));
    }
}

/// <summary>
/// Sessions of a client performed on this trainer's plans in the active tenant. Other professionals' and other tenants'
/// data is never included.
/// </summary>
public sealed record ListClientWorkoutSessionsQuery(Guid ClientId, DateTimeOffset? From, DateTimeOffset? To) : IQuery<IReadOnlyList<WorkoutSessionListItem>>;

internal sealed class ListClientWorkoutSessionsHandler(IReadDbContext db, ICareAccess access)
    : IQueryHandler<ListClientWorkoutSessionsQuery, IReadOnlyList<WorkoutSessionListItem>>
{
    public async Task<Result<IReadOnlyList<WorkoutSessionListItem>>> Handle(ListClientWorkoutSessionsQuery query, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(trainer.Value, query.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var sessions = db.WorkoutSessions.Where(
            s => s.ClientId == query.ClientId && s.TrainerProfileId == trainer.Value.Profile.Id && s.TenantId == trainer.Value.TenantId);
        return Result.Success(await WorkoutMapping.ListAsync(sessions, query.From, query.To, cancellationToken));
    }
}

internal static class WorkoutMapping
{
    public static async Task<IReadOnlyList<WorkoutSessionListItem>> ListAsync(
        IQueryable<WorkoutSession> sessions, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (from is { } f)
        {
            sessions = sessions.Where(s => s.StartedAt >= f);
        }

        if (to is { } t)
        {
            sessions = sessions.Where(s => s.StartedAt <= t);
        }

        return await sessions
            .OrderByDescending(s => s.StartedAt)
            .Take(200)
            .Select(s => new WorkoutSessionListItem(
                s.Id, s.TenantId, s.TrainingPlanId, s.PlanVersionNumber, s.PlannedSessionName, s.Kind, s.Status, s.StartedAt, s.CompletedAt, s.LocalDate,
                s.Feedback == null ? null : s.Feedback.PerceivedExertion,
                s.Feedback != null && s.Feedback.ReportedDiscomfort,
                s.Reply != null))
            .ToListAsync(cancellationToken);
    }

    public static WorkoutSessionResponse ToResponse(WorkoutSession s) =>
        new(
            s.Id, s.TenantId, s.ClientId, s.TrainingPlanId, s.PlanVersionNumber, s.PlannedSessionId, s.PlannedSessionName, s.Kind, s.Status, s.StartedAt,
            s.CompletedAt, s.AbandonedAt, s.LocalDate, (int?)s.ActiveDuration()?.TotalSeconds,
            s.Sets.OrderBy(x => x.LoggedAt).Select(x => new SetLogResponse(
                x.Id, x.ExerciseId, x.PlannedExerciseId, x.SetNumber, x.Repetitions, x.LoadKg, x.DurationSeconds, x.DistanceMeters, x.Difficulty,
                x.Notes, x.LoggedAt, x.Corrections)).ToList(),
            s.Feedback,
            s.Reply,
            s.AppleHealthWorkout);
}
