using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Assessments;

/// <summary>
/// What the client tells their professionals before training (RF-23). Only what the requirement lists is collected, to
/// keep sensitive data to the minimum.
/// </summary>
public sealed record HealthHistoryContent(
    string? Goals,
    string? ExerciseHistory,
    string? Limitations,
    string? ReportedInjuries,
    string? Availability,
    string? Equipment)
{
    /// <summary>
    /// Changes to limitations or injuries may make a prescription unsafe, so they flag the client's training plans for
    /// review. Goals, availability and equipment do not.
    /// </summary>
    public bool IsSignificantChangeFrom(HealthHistoryContent? previous) =>
        previous is null
            ? !string.IsNullOrWhiteSpace(Limitations) || !string.IsNullOrWhiteSpace(ReportedInjuries)
            : !SameText(Limitations, previous.Limitations) || !SameText(ReportedInjuries, previous.ReportedInjuries);

    public HealthHistoryContent Normalized() =>
        new(Clean(Goals), Clean(ExerciseHistory), Clean(Limitations), Clean(ReportedInjuries), Clean(Availability), Clean(Equipment));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool SameText(string? left, string? right) => string.Equals(Clean(left), Clean(right), StringComparison.Ordinal);
}

/// <summary>One submitted version of the history. Earlier revisions are never changed.</summary>
public sealed class HealthHistoryRevision : Entity<Guid>
{
    internal HealthHistoryRevision(Guid id, int number, HealthHistoryContent content, bool isSignificantChange, DateTimeOffset submittedAt)
        : base(id)
    {
        Number = number;
        Content = content;
        IsSignificantChange = isSignificantChange;
        SubmittedAt = submittedAt;
        AcknowledgedAt = submittedAt;
    }

    private HealthHistoryRevision() { }

    public int Number { get; private set; }

    public HealthHistoryContent Content { get; private set; } = null!;

    public bool IsSignificantChange { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    /// <summary>When the client confirmed the information is true and complete to the best of their knowledge.</summary>
    public DateTimeOffset AcknowledgedAt { get; private set; }
}

/// <summary>A professional confirms they read a revision. It records who, for which organization and when.</summary>
public sealed class HealthHistoryReview : Entity<Guid>
{
    internal HealthHistoryReview(Guid id, int revisionNumber, Guid tenantId, ProfessionalProfile reviewer, string? notes, DateTimeOffset reviewedAt)
        : base(id)
    {
        RevisionNumber = revisionNumber;
        TenantId = tenantId;
        ReviewerProfileId = reviewer.Id;
        ReviewerUserId = reviewer.UserId;
        Discipline = reviewer.Discipline;
        Notes = notes;
        ReviewedAt = reviewedAt;
    }

    private HealthHistoryReview() { }

    public int RevisionNumber { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ReviewerProfileId { get; private set; }

    public Guid ReviewerUserId { get; private set; }

    public Discipline Discipline { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset ReviewedAt { get; private set; }
}

/// <summary>
/// The client's own health history, one per client and not owned by any tenant: the client edits it, and professionals
/// read and review it only through an active relationship with a <c>HealthHistory</c> grant.
/// </summary>
public sealed class HealthHistory : AggregateRoot<Guid>
{
    private readonly List<HealthHistoryRevision> _revisions = [];
    private readonly List<HealthHistoryReview> _reviews = [];

    private HealthHistory(Guid id, Guid clientId, DateTimeOffset now) : base(id)
    {
        ClientId = clientId;
        CreatedAt = now;
    }

    private HealthHistory() { }

    public Guid ClientId { get; private set; }

    public int CurrentRevisionNumber { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public IReadOnlyCollection<HealthHistoryRevision> Revisions => _revisions.AsReadOnly();

    public IReadOnlyCollection<HealthHistoryReview> Reviews => _reviews.AsReadOnly();

    public HealthHistoryRevision? Current => _revisions.Find(r => r.Number == CurrentRevisionNumber);

    public static HealthHistory Start(Guid clientId, DateTimeOffset now) => new(Guid.CreateVersion7(now), clientId, now);

    /// <summary>
    /// Records a new revision. The client must acknowledge every submission. Submitting the same content again is a
    /// no-op, so a retried request does not create a second revision.
    /// </summary>
    public Result<HealthHistoryRevision> Submit(HealthHistoryContent content, bool acknowledged, DateTimeOffset now)
    {
        if (!acknowledged)
        {
            return HealthHistoryErrors.AcknowledgementRequired;
        }

        var normalized = content.Normalized();
        var previous = Current;
        if (previous is not null && previous.Content == normalized)
        {
            return previous;
        }

        var significant = normalized.IsSignificantChangeFrom(previous?.Content);
        var revision = new HealthHistoryRevision(Guid.CreateVersion7(now), CurrentRevisionNumber + 1, normalized, significant, now);
        _revisions.Add(revision);
        CurrentRevisionNumber = revision.Number;
        UpdatedAt = now;

        Raise(new HealthHistoryRevised(Id, ClientId, revision.Number, significant, now));
        return revision;
    }

    public Result Review(Guid tenantId, ProfessionalProfile reviewer, int revisionNumber, string? notes, DateTimeOffset now)
    {
        if (!reviewer.IsVerified)
        {
            return ProfessionalErrors.NotVerified;
        }

        if (_revisions.TrueForAll(r => r.Number != revisionNumber))
        {
            return HealthHistoryErrors.RevisionNotFound;
        }

        // Reviewing the same revision twice for the same organization is idempotent.
        if (_reviews.Exists(r => r.RevisionNumber == revisionNumber && r.ReviewerProfileId == reviewer.Id && r.TenantId == tenantId))
        {
            return Result.Success();
        }

        _reviews.Add(new HealthHistoryReview(Guid.CreateVersion7(now), revisionNumber, tenantId, reviewer, notes?.Trim(), now));
        return Result.Success();
    }
}

public sealed record HealthHistoryRevised(Guid HealthHistoryId, Guid ClientId, int RevisionNumber, bool IsSignificantChange, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public interface IHealthHistoryRepository : IRepository<HealthHistory, Guid>
{
    Task<HealthHistory?> GetByClientAsync(Guid clientId, CancellationToken cancellationToken = default);
}

public static class HealthHistoryErrors
{
    public static readonly Error NotFound = Error.NotFound("HealthHistory.NotFound", "No health history has been submitted yet.");
    public static readonly Error RevisionNotFound = Error.NotFound("HealthHistory.RevisionNotFound", "The health history revision was not found.");
    public static readonly Error AcknowledgementRequired = Error.Validation(
        "HealthHistory.AcknowledgementRequired", "Confirm that the information is true and complete before submitting.");
}
