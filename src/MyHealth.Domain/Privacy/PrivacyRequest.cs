using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Privacy;

/// <summary>Data-subject rights handled by the platform (RNF-04, LGPD art. 18).</summary>
public enum PrivacyRequestType
{
    /// <summary>A copy of the user's own data. Fulfilled automatically and downloadable for a limited time.</summary>
    DataExport = 1,

    /// <summary>The user asks for data to be corrected. Handled by a platform operator.</summary>
    Correction = 2,

    /// <summary>The user asks for data to be deleted, subject to retention obligations. Handled by a platform operator.</summary>
    Deletion = 3,
}

public enum PrivacyRequestStatus
{
    Submitted = 1,
    InProgress = 2,
    Completed = 3,
    Rejected = 4,
}

/// <summary>
/// A data-subject request and its handling. Operators see the request and record the outcome; they never see the
/// contents of an export, which only the requester can download (finding 2.1 #5).
/// </summary>
public sealed class PrivacyRequest : AggregateRoot<Guid>
{
    /// <summary>How long an export can be downloaded after it was requested.</summary>
    public static readonly TimeSpan ExportAvailability = TimeSpan.FromDays(7);

    private PrivacyRequest(Guid id) : base(id) { }

    private PrivacyRequest() { }

    public Guid UserId { get; private set; }

    public PrivacyRequestType Type { get; private set; }

    public PrivacyRequestStatus Status { get; private set; }

    /// <summary>What the user wants corrected or deleted, in their own words.</summary>
    public string? Details { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Guid? HandledBy { get; private set; }

    public string? ResolutionNotes { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public int DeliveryCount { get; private set; }

    public DateTimeOffset? LastDeliveredAt { get; private set; }

    public static PrivacyRequest RequestExport(Guid userId, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            UserId = userId,
            Type = PrivacyRequestType.DataExport,
            Status = PrivacyRequestStatus.Completed,
            SubmittedAt = now,
            ResolvedAt = now,
            ExpiresAt = now + ExportAvailability,
        };

    public static Result<PrivacyRequest> Submit(Guid userId, PrivacyRequestType type, string details, DateTimeOffset now)
    {
        if (type == PrivacyRequestType.DataExport)
        {
            return PrivacyErrors.UseExportEndpoint;
        }

        if (string.IsNullOrWhiteSpace(details))
        {
            return PrivacyErrors.DetailsRequired;
        }

        return new PrivacyRequest(Guid.CreateVersion7(now))
        {
            UserId = userId,
            Type = type,
            Status = PrivacyRequestStatus.Submitted,
            Details = details.Trim(),
            SubmittedAt = now,
        };
    }

    public bool IsExportAvailable(DateTimeOffset now) => Type == PrivacyRequestType.DataExport && now < ExpiresAt;

    /// <summary>Counts a download by the requester. Each delivery is also audited.</summary>
    public Result RecordDelivery(Guid userId, DateTimeOffset now)
    {
        if (userId != UserId || Type != PrivacyRequestType.DataExport)
        {
            return PrivacyErrors.NotFound;
        }

        if (!IsExportAvailable(now))
        {
            return PrivacyErrors.ExportExpired;
        }

        DeliveryCount++;
        LastDeliveredAt = now;
        return Result.Success();
    }

    public Result Start(Guid operatorUserId, DateTimeOffset now)
    {
        if (Status != PrivacyRequestStatus.Submitted)
        {
            return PrivacyErrors.NotOpen;
        }

        Status = PrivacyRequestStatus.InProgress;
        StartedAt = now;
        HandledBy = operatorUserId;
        Raise(new PrivacyRequestUpdated(Id, UserId, Status, now));
        return Result.Success();
    }

    public Result Complete(Guid operatorUserId, string? notes, DateTimeOffset now) =>
        Resolve(operatorUserId, PrivacyRequestStatus.Completed, notes, now);

    /// <summary>Rejection must be explained, e.g. a deletion refused because a retention obligation applies.</summary>
    public Result Reject(Guid operatorUserId, string reason, DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(reason) ? PrivacyErrors.ReasonRequired : Resolve(operatorUserId, PrivacyRequestStatus.Rejected, reason, now);

    private Result Resolve(Guid operatorUserId, PrivacyRequestStatus outcome, string? notes, DateTimeOffset now)
    {
        if (Type == PrivacyRequestType.DataExport || Status is not (PrivacyRequestStatus.Submitted or PrivacyRequestStatus.InProgress))
        {
            return PrivacyErrors.NotOpen;
        }

        Status = outcome;
        StartedAt ??= now;
        ResolvedAt = now;
        HandledBy = operatorUserId;
        ResolutionNotes = notes?.Trim();
        Raise(new PrivacyRequestUpdated(Id, UserId, Status, now));
        return Result.Success();
    }
}

public sealed record PrivacyRequestUpdated(Guid RequestId, Guid UserId, PrivacyRequestStatus Status, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public interface IPrivacyRequestRepository : IRepository<PrivacyRequest, Guid>;

public static class PrivacyErrors
{
    public static readonly Error NotFound = Error.NotFound("Privacy.NotFound", "The request was not found.");
    public static readonly Error NotOpen = Error.Conflict("Privacy.NotOpen", "The request has already been resolved.");
    public static readonly Error DetailsRequired = Error.Validation("Privacy.DetailsRequired", "Describe what you want corrected or deleted.");
    public static readonly Error ReasonRequired = Error.Validation("Privacy.ReasonRequired", "Explain why the request is rejected.");
    public static readonly Error ExportExpired = Error.Conflict("Privacy.ExportExpired", "This export has expired; request a new one.");
    public static readonly Error UseExportEndpoint = Error.Validation("Privacy.UseExportEndpoint", "Request a data export through the data-exports endpoint.");
}
