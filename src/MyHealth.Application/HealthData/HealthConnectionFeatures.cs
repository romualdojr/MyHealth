using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.HealthData;

public enum HealthConnectionAction
{
    Connect = 1,
    Pause = 2,
    Revoke = 3,
}

/// <summary>Connect, pause and revoke heart-rate import are separate, explicit client actions (RF-38).</summary>
public sealed record ChangeHealthConnectionCommand(HealthConnectionAction Action) : ICommand;

internal sealed class ChangeHealthConnectionValidator : AbstractValidator<ChangeHealthConnectionCommand>
{
    public ChangeHealthConnectionValidator() => RuleFor(c => c.Action).IsInEnum();
}

internal sealed class ChangeHealthConnectionHandler(
    IUserContext user, IHealthConnectionRepository connections, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ChangeHealthConnectionCommand>
{
    public async Task<Result> Handle(ChangeHealthConnectionCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var connection = await connections.GetAsync(user.UserId, HealthDataProvider.AppleHealth, cancellationToken);
        if (connection is null)
        {
            if (command.Action != HealthConnectionAction.Connect)
            {
                return Result.Success();
            }

            connection = HealthConnection.Create(user.UserId, HealthDataProvider.AppleHealth, now);
            connections.Add(connection);
        }

        var result = Result.Success();
        switch (command.Action)
        {
            case HealthConnectionAction.Connect:
                connection.Reconnect(now);
                break;
            case HealthConnectionAction.Pause:
                result = connection.Pause(now);
                break;
            default:
                connection.Revoke(now);
                break;
        }

        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(
            command.Action == HealthConnectionAction.Connect ? AuditActions.Grant : AuditActions.Revoke,
            $"HealthConnection.{command.Action}", connection.Id, user.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>
/// Opt in or out of saving completed workouts to Apple Health (HK-06). Independent of heart-rate import because iOS
/// grants write permission separately; the phone must still obtain that permission before saving.
/// </summary>
public sealed record SetAppleHealthWorkoutSavingCommand(bool Enabled) : ICommand;

internal sealed class SetAppleHealthWorkoutSavingHandler(
    IUserContext user, IHealthConnectionRepository connections, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SetAppleHealthWorkoutSavingCommand>
{
    public async Task<Result> Handle(SetAppleHealthWorkoutSavingCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var connection = await connections.GetAsync(user.UserId, HealthDataProvider.AppleHealth, cancellationToken);
        if (connection is null)
        {
            if (!command.Enabled)
            {
                return Result.Success();
            }

            connection = HealthConnection.Create(user.UserId, HealthDataProvider.AppleHealth, now);
            connections.Add(connection);
        }

        connection.SetWorkoutSaving(command.Enabled, now);
        audit.Record(
            command.Enabled ? AuditActions.Grant : AuditActions.Revoke, "HealthConnection.WorkoutSaving", connection.Id, user.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>The phone reports the HKWorkout it saved, so a retry or a reinstall never saves a second one.</summary>
public sealed record RecordAppleHealthWorkoutCommand(Guid SessionId, Guid HealthKitWorkoutId, DateTimeOffset SavedAt) : ICommand;

internal sealed class RecordAppleHealthWorkoutValidator : AbstractValidator<RecordAppleHealthWorkoutCommand>
{
    public RecordAppleHealthWorkoutValidator() => RuleFor(c => c.HealthKitWorkoutId).NotEmpty();
}

internal sealed class RecordAppleHealthWorkoutHandler(
    IUserContext user, IWorkoutSessionRepository sessions, IHealthConnectionRepository connections, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RecordAppleHealthWorkoutCommand>
{
    public async Task<Result> Handle(RecordAppleHealthWorkoutCommand command, CancellationToken cancellationToken)
    {
        var session = await sessions.GetByIdAsync(command.SessionId, cancellationToken);
        if (session is null || session.ClientId != user.UserId)
        {
            return WorkoutErrors.NotFound;
        }

        var connection = await connections.GetAsync(user.UserId, HealthDataProvider.AppleHealth, cancellationToken);
        if (connection is not { SaveWorkouts: true } && session.AppleHealthWorkout is null)
        {
            return HealthDataErrors.WorkoutExportNotEnabled;
        }

        var result = session.RecordAppleHealthWorkout(command.HealthKitWorkoutId, command.SavedAt, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record HealthConnectionChangeItem(HealthConnectionStatus Status, DateTimeOffset ChangedAt);

/// <summary>A professional receiving heart rate, and the organization they receive it in (only that tenant's workouts, MT-06).</summary>
public sealed record HeartRateRecipient(
    Guid RelationshipId, Guid TenantId, string OrganizationName, string ProfessionalName, Discipline Discipline, DateTimeOffset SharedSince);

public sealed record ExcludedPeriod(DateTimeOffset From, DateTimeOffset? To);

/// <summary>
/// Everything the Apple Health screen must explain (RF-38): import state and history, the periods excluded from import,
/// whether workouts are saved to Apple Health, and exactly which professionals receive heart rate.
/// </summary>
public sealed record HealthConnectionResponse(
    HealthDataProvider Provider,
    HealthConnectionStatus Status,
    bool SaveWorkoutsToAppleHealth,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<HealthConnectionChangeItem> History,
    IReadOnlyList<ExcludedPeriod> ExcludedPeriods,
    IReadOnlyList<HeartRateRecipient> Recipients);

public sealed record GetHealthConnectionQuery : IQuery<HealthConnectionResponse>;

internal sealed class GetHealthConnectionHandler(IUserContext user, IReadDbContext db) : IQueryHandler<GetHealthConnectionQuery, HealthConnectionResponse>
{
    public async Task<Result<HealthConnectionResponse>> Handle(GetHealthConnectionQuery query, CancellationToken cancellationToken)
    {
        var connection = await db.HealthConnections
            .Include(c => c.History)
            .FirstOrDefaultAsync(c => c.ClientId == user.UserId && c.Provider == HealthDataProvider.AppleHealth, cancellationToken);

        var lastSync = await db.WorkoutHeartRates
            .Where(h => h.ClientId == user.UserId)
            .MaxAsync(h => h.LastSyncedAt, cancellationToken);

        var recipients = connection is { AllowsSharing: false }
            ? []
            : await (
                    from r in db.CareRelationships
                    from g in r.Grants
                    join p in db.ProfessionalProfiles on r.ProfessionalProfileId equals p.Id
                    join t in db.Tenants on r.TenantId equals t.Id
                    where r.ClientId == user.UserId && r.Status == RelationshipStatus.Active
                          && g.Category == DataCategory.HeartRate && g.RevokedAt == null
                    select new HeartRateRecipient(r.Id, r.TenantId, t.Name, p.DisplayName, p.Discipline, g.GrantedAt))
                .ToListAsync(cancellationToken);

        return new HealthConnectionResponse(
            HealthDataProvider.AppleHealth,
            connection?.Status ?? HealthConnectionStatus.NotConnected,
            connection?.SaveWorkouts ?? false,
            lastSync,
            connection?.History.OrderBy(h => h.ChangedAt).Select(h => new HealthConnectionChangeItem(h.Status, h.ChangedAt)).ToList() ?? [],
            connection?.ExcludedIntervals().Select(i => new ExcludedPeriod(i.From, i.To)).ToList() ?? [],
            recipients);
    }
}

public sealed record ReconciliationCandidate(
    Guid SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool HeartRateFinalized,
    int Revision,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset ReconcileUntil,
    bool SavedToAppleHealth);

/// <summary>
/// Finished workouts the phone should re-query in HealthKit (HK-08): late samples from sources that sync after the
/// workout, failed uploads, and source deletions. Empty while import is paused or revoked.
/// </summary>
public sealed record ReconciliationCandidatesResponse(bool ImportAllowed, IReadOnlyList<ReconciliationCandidate> Sessions);

public sealed record ListReconciliationCandidatesQuery : IQuery<ReconciliationCandidatesResponse>;

internal sealed class ListReconciliationCandidatesHandler(IUserContext user, IReadDbContext db, TimeProvider clock)
    : IQueryHandler<ListReconciliationCandidatesQuery, ReconciliationCandidatesResponse>
{
    public async Task<Result<ReconciliationCandidatesResponse>> Handle(ListReconciliationCandidatesQuery query, CancellationToken cancellationToken)
    {
        var importAllowed = await db.HealthConnections.AnyAsync(
            c => c.ClientId == user.UserId && c.Provider == HealthDataProvider.AppleHealth && c.Status == HealthConnectionStatus.Connected,
            cancellationToken);
        if (!importAllowed)
        {
            return new ReconciliationCandidatesResponse(false, []);
        }

        var since = clock.GetUtcNow() - HealthConnection.ReconciliationWindow;
        var rows = await (
                from s in db.WorkoutSessions
                join h in db.WorkoutHeartRates on s.Id equals h.Id into heartRates
                from h in heartRates.DefaultIfEmpty()
                where s.ClientId == user.UserId
                      && (s.Status == WorkoutStatus.Completed || s.Status == WorkoutStatus.Abandoned)
                      && (s.CompletedAt ?? s.AbandonedAt) >= since
                orderby s.StartedAt descending
                select new
                {
                    s.Id,
                    s.StartedAt,
                    EndedAt = (s.CompletedAt ?? s.AbandonedAt)!.Value,
                    Finalized = h != null && h.IsFinalized,
                    Revision = h == null ? 0 : h.Revision,
                    LastSyncedAt = h == null ? null : h.LastSyncedAt,
                    Saved = s.AppleHealthWorkout != null,
                })
            .ToListAsync(cancellationToken);

        return new ReconciliationCandidatesResponse(true, rows
            .Select(r => new ReconciliationCandidate(
                r.Id, r.StartedAt, r.EndedAt, r.Finalized, r.Revision, r.LastSyncedAt, r.EndedAt + HealthConnection.ReconciliationWindow, r.Saved))
            .ToList());
    }
}
