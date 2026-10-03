using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Infrastructure.Persistence;

namespace MyHealth.Infrastructure.Auditing;

internal sealed class AuditLog(ApplicationDbContext db, IUserContext user, TimeProvider clock) : IAuditLog
{
    public void Record(string action, string resourceType, Guid resourceId, Guid? subjectUserId, Guid? tenantId) =>
        db.AuditEntries.Add(AuditEntry.Create(user.UserId, action, resourceType, resourceId, subjectUserId, tenantId, clock.GetUtcNow()));

    public async Task RecordReadAsync(string resourceType, Guid resourceId, Guid? subjectUserId, Guid? tenantId, CancellationToken cancellationToken)
    {
        Record(AuditActions.Read, resourceType, resourceId, subjectUserId, tenantId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordDeniedAsync(string resourceType, Guid resourceId, Guid? tenantId, CancellationToken cancellationToken)
    {
        // No subject: the denied caller must not learn, even indirectly, whose resource it was.
        Record(AuditActions.Deny, resourceType, resourceId, subjectUserId: null, tenantId);
        await db.SaveChangesAsync(cancellationToken);
    }
}
