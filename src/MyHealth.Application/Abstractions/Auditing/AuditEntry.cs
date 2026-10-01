namespace MyHealth.Application.Abstractions.Auditing;

/// <summary>
/// Who did what to which record, and whose data it was (RNF-03). Never contains clinical content.
/// </summary>
public sealed class AuditEntry
{
    private AuditEntry() { }

    public Guid Id { get; private init; }

    public DateTimeOffset OccurredAt { get; private init; }

    public Guid ActorUserId { get; private init; }

    public string Action { get; private init; } = null!;

    public string ResourceType { get; private init; } = null!;

    public Guid ResourceId { get; private init; }

    /// <summary>The person the data belongs to, when different from the actor.</summary>
    public Guid? SubjectUserId { get; private init; }

    public static AuditEntry Create(
        Guid actorUserId, string action, string resourceType, Guid resourceId, Guid? subjectUserId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            SubjectUserId = subjectUserId,
        };
}

public static class AuditActions
{
    public const string Read = "read";
    public const string Create = "create";
    public const string Update = "update";
    public const string Publish = "publish";
    public const string Grant = "grant";
    public const string Revoke = "revoke";
    public const string End = "end";
    public const string Verify = "verify";
    public const string Reject = "reject";
    public const string Import = "import";
}

public interface IAuditLog
{
    /// <summary>Adds an entry to the current unit of work; it is saved with the command's changes.</summary>
    void Record(string action, string resourceType, Guid resourceId, Guid? subjectUserId = null);

    /// <summary>Saves an entry immediately. Used by queries that read sensitive data.</summary>
    Task RecordReadAsync(string resourceType, Guid resourceId, Guid? subjectUserId, CancellationToken cancellationToken);
}
