using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Training.Exercises;

namespace MyHealth.Domain.Training.MotionCapture;

public enum MotionCaptureStatus
{
    /// <summary>The video is stored; the worker has not sent it to the provider yet.</summary>
    Queued = 1,

    /// <summary>The provider is turning the video into a 3D animation.</summary>
    Processing = 2,

    Succeeded = 3,

    Failed = 4,
}

/// <summary>A file the provider produced, copied to MyHealth's media storage.</summary>
public sealed record MotionCaptureOutput(MediaKind Kind, CameraAngle? Angle, string Format, string MediaKey, string Url, long SizeBytes);

/// <summary>
/// A trainer's video of one exercise being turned into a 3D model (and rendered 3D videos) by a motion-capture provider
/// such as DeepMotion. The source video shows a person, so the trainer consents to sending it to the provider, and it
/// is deleted from MyHealth once processing ends. Nothing reaches the exercise until the trainer accepts the result.
/// </summary>
public sealed class MotionCaptureJob : AggregateRoot<Guid>
{
    public const int CurrentConsentVersion = 1;
    public const int MaxDurationSeconds = 60;
    public const int MaxTransientErrors = 8;

    private MotionCaptureJob(Guid id) : base(id) { }

    private MotionCaptureJob() { }

    public Guid TenantId { get; private set; }

    public Guid ExerciseId { get; private set; }

    public Guid RequestedByProfileId { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public string Provider { get; private set; } = null!;

    public string? ProviderJobId { get; private set; }

    public string SourceFileName { get; private set; } = null!;

    public string SourceContentType { get; private set; } = null!;

    public long SourceSizeBytes { get; private set; }

    /// <summary>Where the source video is stored until processing ends; <c>null</c> once deleted.</summary>
    public string? SourceMediaKey { get; private set; }

    public int DurationSeconds { get; private set; }

    public int ConsentVersion { get; private set; }

    public MotionCaptureStatus Status { get; private set; }

    public int ProgressPercent { get; private set; }

    public int TransientErrors { get; private set; }

    public string? Error { get; private set; }

    public List<MotionCaptureOutput> Outputs { get; private set; } = [];

    /// <summary>When the worker should look at the job again (also its lease while one instance works on it).</summary>
    public DateTimeOffset NextCheckAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? SourceDeletedAt { get; private set; }

    public static Result<MotionCaptureJob> Create(
        Guid id, Exercise exercise, Guid tenantId, Guid profileId, Guid userId, string provider, string sourceKey, string fileName, string contentType,
        long sizeBytes, int durationSeconds, int consentVersion, DateTimeOffset now)
    {
        if (consentVersion != CurrentConsentVersion)
        {
            return MotionCaptureErrors.ConsentRequired;
        }

        if (!contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || sizeBytes <= 0)
        {
            return MotionCaptureErrors.NotAVideo;
        }

        if (durationSeconds is < 1 or > MaxDurationSeconds)
        {
            return MotionCaptureErrors.TooLong;
        }

        return new MotionCaptureJob(id)
        {
            TenantId = tenantId,
            ExerciseId = exercise.Id,
            RequestedByProfileId = profileId,
            RequestedByUserId = userId,
            Provider = provider,
            SourceMediaKey = sourceKey,
            SourceFileName = fileName,
            SourceContentType = contentType,
            SourceSizeBytes = sizeBytes,
            DurationSeconds = durationSeconds,
            ConsentVersion = consentVersion,
            Status = MotionCaptureStatus.Queued,
            NextCheckAt = now,
            CreatedAt = now,
        };
    }

    public bool IsFinished => Status is MotionCaptureStatus.Succeeded or MotionCaptureStatus.Failed;

    public void MarkSubmitted(string providerJobId, DateTimeOffset now, TimeSpan checkAfter)
    {
        ProviderJobId = providerJobId;
        Status = MotionCaptureStatus.Processing;
        SubmittedAt = now;
        TransientErrors = 0;
        NextCheckAt = now + checkAfter;
    }

    public void UpdateProgress(int percent, DateTimeOffset now, TimeSpan checkAfter)
    {
        ProgressPercent = Math.Clamp(percent, ProgressPercent, 99);
        TransientErrors = 0;
        NextCheckAt = now + checkAfter;
    }

    public void Succeed(IReadOnlyList<MotionCaptureOutput> outputs, DateTimeOffset now)
    {
        Outputs = [.. outputs];
        Status = MotionCaptureStatus.Succeeded;
        ProgressPercent = 100;
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = MotionCaptureStatus.Failed;
        Error = error.Length > 500 ? error[..500] : error;
        CompletedAt = now;
    }

    /// <summary>The provider could not be reached: try again with back-off (1, 2, 4… minutes), then give up.</summary>
    public void RecordTransientError(DateTimeOffset now)
    {
        TransientErrors++;
        if (TransientErrors >= MaxTransientErrors)
        {
            Fail("O serviço de captura de movimento não respondeu. Tente enviar o vídeo novamente.", now);
            return;
        }

        NextCheckAt = now + TimeSpan.FromMinutes(Math.Pow(2, TransientErrors - 1));
    }

    public void SourceDeleted(DateTimeOffset now)
    {
        SourceMediaKey = null;
        SourceDeletedAt = now;
    }

    /// <summary>The trainer put the result on the exercise. Accepting again is harmless.</summary>
    public Result Accept(DateTimeOffset now)
    {
        if (Status != MotionCaptureStatus.Succeeded)
        {
            return MotionCaptureErrors.NotReady;
        }

        AcceptedAt ??= now;
        return Result.Success();
    }
}

public interface IMotionCaptureJobRepository : IRepository<MotionCaptureJob, Guid>
{
    /// <summary>Jobs the worker should look at now (queued or processing, due), oldest first.</summary>
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a job for <paramref name="until"/> if it is still due, so two workers never handle it at once. Returns
    /// <c>false</c> when another instance took it first.
    /// </summary>
    Task<bool> TryClaimAsync(Guid jobId, DateTimeOffset now, DateTimeOffset until, CancellationToken cancellationToken = default);

    /// <summary>Loads the job as it is in the database now (after a claim changed it), even if an older copy is already loaded.</summary>
    Task<MotionCaptureJob?> GetCurrentAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<int> CountForTenantSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken = default);
}

public static class MotionCaptureErrors
{
    public static readonly Error ConsentRequired = Error.Validation(
        "MotionCapture.ConsentRequired", "Accept the current terms for sending the video to the motion-capture provider.");
    public static readonly Error NotAVideo = Error.Validation("MotionCapture.NotAVideo", "Send a video file (MP4 or MOV).");
    public static readonly Error TooLong = Error.Validation("MotionCapture.TooLong", $"The video must be between 1 and {MaxSeconds} seconds long.");
    public static readonly Error TooLarge = Error.Validation("MotionCapture.TooLarge", "The video file is too large.");
    public static readonly Error MonthlyLimitReached = Error.Conflict("MotionCapture.MonthlyLimitReached", "Your organization reached this month's limit of 3D conversions.");
    public static readonly Error NotFound = Error.NotFound("MotionCapture.NotFound", "The 3D conversion was not found.");
    public static readonly Error NotReady = Error.Conflict("MotionCapture.NotReady", "The 3D conversion has not finished successfully.");

    private const int MaxSeconds = MotionCaptureJob.MaxDurationSeconds;
}
