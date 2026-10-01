using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Infrastructure.Persistence;

namespace MyHealth.Infrastructure.Auditing;

internal sealed class AuditLog(ApplicationDbContext db, IUserContext user, TimeProvider clock) : IAuditLog
{
    public void Record(string action, string resourceType, Guid resourceId, Guid? subjectUserId = null) =>
        db.AuditEntries.Add(AuditEntry.Create(user.UserId, action, resourceType, resourceId, subjectUserId, clock.GetUtcNow()));

    public async Task RecordReadAsync(string resourceType, Guid resourceId, Guid? subjectUserId, CancellationToken cancellationToken)
    {
        Record(AuditActions.Read, resourceType, resourceId, subjectUserId);
        await db.SaveChangesAsync(cancellationToken);
    }
}
