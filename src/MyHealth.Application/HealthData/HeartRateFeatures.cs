using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.HealthData;

/// <summary>Matches <c>WorkoutHeartRatePoint</c> in the iOS module.</summary>
public sealed record HeartRatePointInput(
    Guid HealthKitId, DateTimeOffset Timestamp, double BeatsPerMinute, string SourceName, string? DeviceName, bool WasUserEntered = false);

/// <summary>
/// Matches <c>WorkoutHeartRateReport</c> sent by the iOS module (HK-04/HK-05). The phone's average and maximum are
/// accepted for compatibility but ignored: the API recalculates from accepted points.
/// </summary>
public sealed record SyncWorkoutHeartRateCommand(
    Guid SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<HeartRatePointInput> Points) : ICommand<HeartRateSyncResponse>;

public sealed record HeartRateSyncResponse(
    Guid SessionId,
    HeartRateSyncKind Kind,
    int Accepted,
    int Duplicates,
    int Corrected,
    int Rejected,
    int Removed,
    int ClaimedByOtherSession,
    int Revision,
    bool IsFinalized,
    HeartRateSummary? Summary,
    DateTimeOffset SyncedAt);

internal sealed class SyncWorkoutHeartRateValidator : AbstractValidator<SyncWorkoutHeartRateCommand>
{
    public SyncWorkoutHeartRateValidator()
    {
        RuleFor(c => c.SessionId).NotEmpty();
        RuleFor(c => c.EndedAt).GreaterThanOrEqualTo(c => c.StartedAt).When(c => c.EndedAt is not null);
        RuleFor(c => c.Points).NotNull().Must(p => p.Count <= 20_000).WithMessage("Send at most 20,000 points per report.");
        RuleForEach(c => c.Points).ChildRules(point =>
        {
            point.RuleFor(p => p.HealthKitId).NotEmpty();
            point.RuleFor(p => p.SourceName).NotEmpty().MaximumLength(200);
            point.RuleFor(p => p.DeviceName).MaximumLength(200);
        });
    }
}

internal sealed class SyncWorkoutHeartRateHandler(
    IUserContext user,
    IWorkoutSessionRepository sessions,
    IWorkoutHeartRateRepository heartRates,
    IHealthConnectionRepository connections,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<SyncWorkoutHeartRateCommand, HeartRateSyncResponse>
{
    public async Task<Result<HeartRateSyncResponse>> Handle(SyncWorkoutHeartRateCommand command, CancellationToken cancellationToken)
    {
        var session = await sessions.GetByIdAsync(command.SessionId, cancellationToken);
        if (session is null || session.ClientId != user.UserId)
        {
            return WorkoutErrors.NotFound;
        }

        var connection = await connections.GetAsync(user.UserId, HealthDataProvider.AppleHealth, cancellationToken);
        if (connection is null || !connection.AllowsImport)
        {
            return HealthDataErrors.ImportNotAllowed;
        }

        var now = clock.GetUtcNow();
        if (session.EndedAt is { } ended && now - ended > HealthConnection.ReconciliationWindow)
        {
            return HealthDataErrors.ReconciliationWindowClosed;
        }

        var heartRate = await heartRates.GetByIdAsync(session.Id, cancellationToken);
        if (heartRate is null)
        {
            heartRate = WorkoutHeartRate.Create(session.Id, session.ClientId);
            heartRates.Add(heartRate);
        }

        var readings = command.Points
            .Select(p => new HeartRateReading(p.HealthKitId, p.Timestamp, p.BeatsPerMinute, p.SourceName.Trim(), p.DeviceName, p.WasUserEntered))
            .ToList();
        var claimed = await heartRates.GetClaimedByOtherSessionsAsync(
            session.ClientId, session.Id, readings.Select(r => r.ExternalId).Distinct().ToList(), cancellationToken);

        var outcome = heartRate.Apply(
            new HeartRateReport(command.EndedAt is not null, readings),
            WindowFor(session, connection, command.EndedAt, now),
            claimed,
            now);

        audit.Record(AuditActions.Import, nameof(WorkoutHeartRate), session.Id, session.ClientId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new HeartRateSyncResponse(
            session.Id, outcome.Kind, outcome.Accepted, outcome.Duplicates, outcome.Corrected, outcome.Rejected, outcome.Removed,
            outcome.ClaimedByOtherSession, outcome.Revision, heartRate.IsFinalized, heartRate.Summary, now);
    }

    /// <summary>
    /// The server's session interval is authoritative. While the session is open, the end is the phone's reported end
    /// (when plausible) or "now", allowing for device clock skew. Periods when import was paused or revoked are excluded.
    /// </summary>
    private static HeartRateWindow WindowFor(WorkoutSession session, HealthConnection connection, DateTimeOffset? reportedEnd, DateTimeOffset now)
    {
        var latestAllowed = now + WorkoutSession.ClockSkewTolerance;
        var end = session.EndedAt
                  ?? (reportedEnd is { } e && e <= latestAllowed ? e : latestAllowed);
        return new HeartRateWindow(session.StartedAt, end, connection.ExcludedIntervals());
    }
}

public sealed record HeartRatePointResponse(DateTimeOffset Timestamp, double BeatsPerMinute, string SourceName, string? DeviceName, string Kind);

public sealed record HeartRateGap(DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// Heart rate for one workout. <see cref="DataKind"/> is "measured", "self-reported" or "mixed" (RF-43); each point
/// carries its own kind. Gaps are listed so charts never imply continuous measurement (RF-41).
/// </summary>
public sealed record WorkoutHeartRateResponse(
    Guid SessionId,
    string DataKind,
    string Provider,
    bool IsFinalized,
    int Revision,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? LastRevisedAt,
    DateTimeOffset? LatestSampleAt,
    HeartRateSummary? Summary,
    IReadOnlyList<string> Sources,
    IReadOnlyList<HeartRatePointResponse> Points,
    IReadOnlyList<HeartRateGap> Gaps);

public sealed record GetWorkoutHeartRateQuery(Guid SessionId) : IQuery<WorkoutHeartRateResponse>;

internal sealed class GetWorkoutHeartRateHandler(IUserContext user, IReadDbContext db) : IQueryHandler<GetWorkoutHeartRateQuery, WorkoutHeartRateResponse>
{
    public async Task<Result<WorkoutHeartRateResponse>> Handle(GetWorkoutHeartRateQuery query, CancellationToken cancellationToken)
    {
        var heartRate = await HeartRateMapping.LoadAsync(db, query.SessionId, cancellationToken);
        return heartRate is null || heartRate.ClientId != user.UserId
            ? HealthDataErrors.NotFound
            : HeartRateMapping.ToResponse(heartRate);
    }
}

/// <summary>
/// A professional reads heart rate shared through an active relationship and an active grant, while the client's
/// Apple Health connection is not revoked. Every read is audited.
/// </summary>
public sealed record GetSharedWorkoutHeartRateQuery(Guid RelationshipId, Guid SessionId) : IQuery<WorkoutHeartRateResponse>;

internal sealed class GetSharedWorkoutHeartRateHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<GetSharedWorkoutHeartRateQuery, WorkoutHeartRateResponse>
{
    public async Task<Result<WorkoutHeartRateResponse>> Handle(GetSharedWorkoutHeartRateQuery query, CancellationToken cancellationToken)
    {
        var relationship = await HeartRateMapping.GetSharingRelationshipAsync(access, db, query.RelationshipId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var heartRate = await HeartRateMapping.LoadAsync(db, query.SessionId, cancellationToken);
        if (heartRate is null || heartRate.ClientId != relationship.Value.ClientId)
        {
            return HealthDataErrors.NotFound;
        }

        await audit.RecordReadAsync(nameof(WorkoutHeartRate), heartRate.Id, heartRate.ClientId, cancellationToken);
        return HeartRateMapping.ToResponse(heartRate);
    }
}

public sealed record SharedHeartRateSessionItem(
    Guid SessionId, string PlannedSessionName, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, bool IsFinalized, HeartRateSummary? Summary);

public sealed record ListSharedHeartRateSessionsQuery(Guid RelationshipId) : IQuery<IReadOnlyList<SharedHeartRateSessionItem>>;

internal sealed class ListSharedHeartRateSessionsHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListSharedHeartRateSessionsQuery, IReadOnlyList<SharedHeartRateSessionItem>>
{
    public async Task<Result<IReadOnlyList<SharedHeartRateSessionItem>>> Handle(ListSharedHeartRateSessionsQuery query, CancellationToken cancellationToken)
    {
        var relationship = await HeartRateMapping.GetSharingRelationshipAsync(access, db, query.RelationshipId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var items = await (
                from h in db.WorkoutHeartRates
                join s in db.WorkoutSessions on h.Id equals s.Id
                where h.ClientId == relationship.Value.ClientId
                orderby s.StartedAt descending
                select new SharedHeartRateSessionItem(s.Id, s.PlannedSessionName, s.StartedAt, s.CompletedAt, h.IsFinalized, h.Summary))
            .Take(200)
            .ToListAsync(cancellationToken);

        await audit.RecordReadAsync("WorkoutHeartRate.List", relationship.Value.Id, relationship.Value.ClientId, cancellationToken);
        return items;
    }
}

internal static class HeartRateMapping
{
    public const string AppleHealthProvider = "Apple Health";

    public static async Task<Result<CareRelationship>> GetSharingRelationshipAsync(
        ICareAccess access, IReadDbContext db, Guid relationshipId, CancellationToken cancellationToken)
    {
        var relationship = await access.GetSharedAccessAsync(relationshipId, DataCategory.HeartRate, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship;
        }

        var revoked = await db.HealthConnections.AnyAsync(
            c => c.ClientId == relationship.Value.ClientId && c.Provider == HealthDataProvider.AppleHealth && c.Status == HealthConnectionStatus.Revoked,
            cancellationToken);
        return revoked ? HealthDataErrors.SharingStopped : relationship;
    }

    public static Task<WorkoutHeartRate?> LoadAsync(IReadDbContext db, Guid sessionId, CancellationToken cancellationToken) =>
        db.WorkoutHeartRates.Include(h => h.Samples).FirstOrDefaultAsync(h => h.Id == sessionId, cancellationToken);

    public static WorkoutHeartRateResponse ToResponse(WorkoutHeartRate heartRate)
    {
        var active = heartRate.Samples.Where(s => s.IsActive).OrderBy(s => s.Timestamp).ToList();
        var kinds = active.Select(s => s.Kind).Distinct().ToList();
        var dataKind = kinds.Count switch
        {
            0 or 1 when !kinds.Contains(HeartRateDataKind.SelfReported) => KindName(HeartRateDataKind.Measured),
            1 => KindName(HeartRateDataKind.SelfReported),
            _ => "mixed",
        };

        return new WorkoutHeartRateResponse(
            heartRate.Id,
            dataKind,
            AppleHealthProvider,
            heartRate.IsFinalized,
            heartRate.Revision,
            heartRate.LastSyncedAt,
            heartRate.LastRevisedAt,
            active.LastOrDefault()?.Timestamp,
            heartRate.Summary,
            active.Select(s => s.SourceName).Distinct().Order().ToList(),
            active.Select(s => new HeartRatePointResponse(s.Timestamp, s.BeatsPerMinute, s.SourceName, s.DeviceName, KindName(s.Kind))).ToList(),
            heartRate.FindGaps(WorkoutHeartRate.GapThreshold).Select(g => new HeartRateGap(g.From, g.To)).ToList());
    }

    private static string KindName(HeartRateDataKind kind) => kind == HeartRateDataKind.SelfReported ? "self-reported" : "measured";
}
