namespace MyHealth.Application.Abstractions.Auditing;

/// <summary>
/// Who did what to which record, whose data it was, and in which tenant (RNF-03, MT-04). Never contains clinical content.
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

    /// <summary>The tenant that owns the resource; <c>null</c> for account-level and platform actions.</summary>
    public Guid? TenantId { get; private init; }

    public static AuditEntry Create(
        Guid actorUserId, string action, string resourceType, Guid resourceId, Guid? subjectUserId, Guid? tenantId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            SubjectUserId = subjectUserId,
            TenantId = tenantId,
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
    public const string Invite = "invite";
    public const string Join = "join";
    public const string Correct = "correct";
    public const string Review = "review";
    public const string Comment = "comment";
    public const string Request = "request";
    public const string Deliver = "deliver";
    public const string Resolve = "resolve";
    public const string Suspend = "suspend";
    public const string Archive = "archive";

    /// <summary>An attempt to reach another tenant's resource or a tenant the caller does not belong to (MT-09).</summary>
    public const string Deny = "deny";
}

public interface IAuditLog
{
    /// <summary>Adds an entry to the current unit of work; it is saved with the command's changes.</summary>
    void Record(string action, string resourceType, Guid resourceId, Guid? subjectUserId, Guid? tenantId);

    /// <summary>Saves an entry immediately. Used by queries that read sensitive data.</summary>
    Task RecordReadAsync(string resourceType, Guid resourceId, Guid? subjectUserId, Guid? tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a denied attempt immediately, since the request fails and nothing else is committed. Call it before the
    /// handler changes anything.
    /// </summary>
    Task RecordDeniedAsync(string resourceType, Guid resourceId, Guid? tenantId, CancellationToken cancellationToken);
}
