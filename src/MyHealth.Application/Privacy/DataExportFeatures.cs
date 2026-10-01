using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Assessments;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Privacy;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Therapies;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Privacy;

public sealed record DataExportResponse(Guid Id, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, string DownloadPath);

/// <summary>
/// The user requests a copy of their own data (RNF-04). The package is assembled when downloaded, so it always reflects
/// the current records; the request and every download are audited (acceptance test 9).
/// </summary>
public sealed record RequestDataExportCommand : ICommand<DataExportResponse>;

internal sealed class RequestDataExportHandler(
    IUserContext user, IUserProfileRepository profiles, IPrivacyRequestRepository requests, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RequestDataExportCommand, DataExportResponse>
{
    public async Task<Result<DataExportResponse>> Handle(RequestDataExportCommand command, CancellationToken cancellationToken)
    {
        if (await profiles.GetByIdAsync(user.UserId, cancellationToken) is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var request = PrivacyRequest.RequestExport(user.UserId, clock.GetUtcNow());
        requests.Add(request);
        audit.Record(AuditActions.Request, DataExport.ResourceType, request.Id, user.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DataExportResponse(request.Id, request.SubmittedAt, request.ExpiresAt!.Value, DataExport.DownloadPath(request.Id));
    }
}

/// <summary>Downloads the package. Only the requester can, only before it expires; each delivery is counted and audited.</summary>
public sealed record DownloadDataExportCommand(Guid RequestId) : ICommand<DataExportPackage>;

internal sealed class DownloadDataExportHandler(
    IUserContext user, IPrivacyRequestRepository requests, IReadDbContext db, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<DownloadDataExportCommand, DataExportPackage>
{
    public async Task<Result<DataExportPackage>> Handle(DownloadDataExportCommand command, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(command.RequestId, cancellationToken);
        if (request is null || request.UserId != user.UserId)
        {
            return PrivacyErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        var delivered = request.RecordDelivery(user.UserId, now);
        if (delivered.IsFailure)
        {
            return delivered.Error;
        }

        var package = await new DataExportAssembler(db, user.UserId).AssembleAsync(request.Id, now, cancellationToken);
        audit.Record(AuditActions.Deliver, DataExport.ResourceType, request.Id, user.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return package;
    }
}

internal static class DataExport
{
    public const string ResourceType = "DataExport";

    public static string DownloadPath(Guid requestId) => $"/api/v1/me/data-exports/{requestId}/download";
}

// ---- Package -------------------------------------------------------------------------------------------------------
// Every record carries its provenance: the organization it belongs to, the professional responsible, when it was made
// and, for measurements, how. Only the subject's own records are included; records the subject keeps about others (as a
// professional) are not theirs to export and are left out.

public sealed record DataExportPackage(
    string Format,
    Guid ExportId,
    DateTimeOffset GeneratedAt,
    ExportSubject Subject,
    IReadOnlyList<ExportProfessionalProfile> ProfessionalProfiles,
    IReadOnlyList<ExportMembership> Memberships,
    IReadOnlyList<ExportRelationship> CareRelationships,
    ExportHealthHistory? HealthHistory,
    IReadOnlyList<ExportPhysicalAssessment> PhysicalAssessments,
    IReadOnlyList<ExportTrainingPlanVersion> TrainingPlans,
    IReadOnlyList<ExportWorkoutSession> WorkoutSessions,
    ExportAppleHealth AppleHealth,
    IReadOnlyList<ExportNutritionAssessment> NutritionAssessments,
    IReadOnlyList<ExportMealPlanVersion> MealPlans,
    IReadOnlyList<ExportFoodDiaryEntry> FoodDiary,
    IReadOnlyList<ExportTherapySession> TherapySessions,
    IReadOnlyList<ExportTherapyConsent> TherapyConsents,
    IReadOnlyList<ExportCarePlan> TherapyCarePlans,
    IReadOnlyList<ExportNotification> Notifications,
    IReadOnlyList<ExportPrivacyRequest> PrivacyRequests,
    IReadOnlyList<ExportAccessLogEntry> AccessLog);

public sealed record ExportSubject(Guid UserId, string FullName, string Email, string TimeZoneId, DateTimeOffset CreatedAt);

public sealed record ExportProfessionalProfile(
    Guid Id, Discipline Discipline, string DisplayName, string? Bio, ProfessionalRegistration Registration, VerificationStatus VerificationStatus,
    DateTimeOffset? VerifiedAt, DateTimeOffset CreatedAt);

public sealed record ExportMembership(Guid TenantId, string OrganizationName, IReadOnlyList<Discipline> Disciplines, bool IsAdministrator, MembershipStatus Status,
    DateTimeOffset? JoinedAt, DateTimeOffset? RevokedAt);

public sealed record ExportGrant(DataCategory Category, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt);

public sealed record ExportRelationship(
    Guid Id, Guid TenantId, string OrganizationName, Discipline Discipline, string ProfessionalName, RelationshipStatus Status, DateTimeOffset InvitedAt,
    DateTimeOffset? RespondedAt, DateTimeOffset? EndedAt, string? EndReason, IReadOnlyList<ExportGrant> Grants);

public sealed record ExportHealthHistoryReview(int RevisionNumber, string OrganizationName, string ReviewerName, Discipline Discipline, string? Notes, DateTimeOffset ReviewedAt);

public sealed record ExportHealthHistory(IReadOnlyList<HealthHistoryRevisionResponse> Revisions, IReadOnlyList<ExportHealthHistoryReview> Reviews);

public sealed record ExportPhysicalAssessment(
    Guid Id, string OrganizationName, string AssessorName, DateOnly AssessedOn, IReadOnlyList<Measurement> Measurements, string? Notes, int Version,
    IReadOnlyList<PhysicalAssessmentCorrection> Corrections, DateTimeOffset CreatedAt);

public sealed record ExportTrainingPlanVersion(
    Guid PlanId, string OrganizationName, string TrainerName, int VersionNumber, PlanDetails Details, PlanContent Content, string ChangeDescription,
    DateTimeOffset PublishedAt);

public sealed record ExportSet(Guid ExerciseId, int SetNumber, int? Repetitions, decimal? LoadKg, int? DurationSeconds, decimal? DistanceMeters, int? Difficulty,
    string? Notes, DateTimeOffset LoggedAt, string Source, IReadOnlyList<SetLogCorrection> Corrections);

public sealed record ExportHeartRateSample(
    DateTimeOffset Timestamp, double BeatsPerMinute, string SourceName, string? DeviceName, HeartRateDataKind Kind, DateTimeOffset ReceivedAt,
    DateTimeOffset? CorrectedAt, DateTimeOffset? RemovedBySourceAt);

public sealed record ExportHeartRate(bool IsFinalized, int Revision, HeartRateSummary? Summary, IReadOnlyList<ExportHeartRateSample> Samples);

public sealed record ExportWorkoutSession(
    Guid Id, string OrganizationName, Guid TrainingPlanId, int PlanVersionNumber, string PlannedSessionName, WorkoutStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, DateTimeOffset? AbandonedAt, DateOnly? LocalDate, IReadOnlyList<ExportSet> Sets, WorkoutFeedback? Feedback, string? TrainerReply,
    DateTimeOffset? TrainerRepliedAt, ExternalWorkoutExport? AppleHealthWorkout, ExportHeartRate? HeartRate);

public sealed record ExportAppleHealth(HealthConnectionStatus Status, bool SaveWorkouts, IReadOnlyList<ExportConnectionChange> History);

public sealed record ExportConnectionChange(HealthConnectionStatus Status, DateTimeOffset ChangedAt);

public sealed record ExportNutritionAssessment(
    Guid Id, string OrganizationName, string DietitianName, NutritionAssessmentContent Content, int Version, IReadOnlyList<NutritionAssessmentCorrection> Corrections,
    DateTimeOffset CreatedAt);

public sealed record ExportMealPlanVersion(
    Guid PlanId, string OrganizationName, string DietitianName, int VersionNumber, MealPlanDetails Details, MealPlanContent Content, string ChangeDescription,
    DateTimeOffset PublishedAt);

public sealed record ExportDiaryComment(string AuthorName, string Message, DateTimeOffset CreatedAt);

public sealed record ExportFoodDiaryEntry(
    Guid Id, string? SharedWithOrganization, Guid? MealPlanId, int? MealPlanVersionNumber, string? MealName, DateTimeOffset EatenAt, DateOnly LocalDate,
    string? Description, int? WaterMl, MealAdherence? Adherence, string? ClientNotes, IReadOnlyList<ExportDiaryComment> Comments, DateTimeOffset RecordedAt);

public sealed record ExportTherapySession(
    Guid Id, string OrganizationName, string PractitionerName, string ModalityCode, string ServiceName, DateTimeOffset StartsAt, TherapySessionStatus Status,
    DateTimeOffset? CompletedAt, DateTimeOffset? CancelledAt, TherapyRecord? Record, IReadOnlyList<TherapyRecordCorrection> Corrections,
    ClientProgressReport? ClientReport, IReadOnlyList<MedicalReferral> Referrals);

public sealed record ExportTherapyConsent(
    string OrganizationName, string PractitionerName, string ModalityCode, int TemplateVersion, IReadOnlyList<string> DisclosedRisks, ConsentDecision Decision,
    DateTimeOffset DecidedAt, DateTimeOffset? WithdrawnAt, string? WithdrawalReason);

public sealed record ExportCarePlan(
    string OrganizationName, string PractitionerName, string ModalityCode, string Goals, int? PlannedSessions, string? Frequency, CarePlanStatus Status,
    IReadOnlyList<CarePlanReview> Reviews, DateTimeOffset CreatedAt);

public sealed record ExportNotification(NotificationType Type, string? OrganizationName, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record ExportPrivacyRequest(PrivacyRequestType Type, PrivacyRequestStatus Status, string? Details, DateTimeOffset SubmittedAt, DateTimeOffset? ResolvedAt,
    string? ResolutionNotes);

/// <summary>
/// Who accessed or changed the subject's data. Actors are named only when they are the subject or one of their own
/// professionals (whose public name the subject already knows); anyone else is described by role.
/// </summary>
public sealed record ExportAccessLogEntry(DateTimeOffset OccurredAt, string Action, string ResourceType, string? OrganizationName, string Actor);

internal sealed class DataExportAssembler(IReadDbContext db, Guid userId)
{
    public const string Format = "myhealth-export/1.0";
    private const string SelfReported = "self-reported";

    private Dictionary<Guid, string> _tenants = [];
    private Dictionary<Guid, string> _professionals = [];

    public async Task<DataExportPackage> AssembleAsync(Guid exportId, DateTimeOffset now, CancellationToken ct)
    {
        var subject = await db.UserProfiles.FirstAsync(u => u.Id == userId, ct);
        var relationships = await db.CareRelationships.Include(r => r.Grants).Where(r => r.ClientId == userId).OrderBy(r => r.InvitedAt).ToListAsync(ct);
        _professionals = await ProfessionalNamesAsync(relationships, ct);

        // Every care record is created through a relationship, so these are all the organizations the package can name.
        var tenantIds = relationships.Select(r => r.TenantId)
            .Concat(await db.TenantMemberships.Where(m => m.UserId == userId).Select(m => m.TenantId).ToListAsync(ct))
            .Distinct()
            .ToList();
        _tenants = await db.Tenants.Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        return new DataExportPackage(
            Format,
            exportId,
            now,
            new ExportSubject(subject.Id, subject.FullName, subject.Email, subject.TimeZoneId, subject.CreatedAt),
            await OwnProfessionalProfilesAsync(ct),
            await MembershipsAsync(ct),
            relationships.Select(r => new ExportRelationship(
                    r.Id, r.TenantId, Tenant(r.TenantId), r.Discipline, Professional(r.ProfessionalProfileId), r.Status, r.InvitedAt, r.RespondedAt, r.EndedAt, r.EndReason,
                    r.Grants.OrderBy(g => g.GrantedAt).Select(g => new ExportGrant(g.Category, g.GrantedAt, g.RevokedAt)).ToList()))
                .ToList(),
            await HealthHistoryAsync(ct),
            await PhysicalAssessmentsAsync(ct),
            await TrainingPlansAsync(ct),
            await WorkoutSessionsAsync(ct),
            await AppleHealthAsync(ct),
            await NutritionAssessmentsAsync(ct),
            await MealPlansAsync(ct),
            await FoodDiaryAsync(ct),
            await TherapySessionsAsync(ct),
            await TherapyConsentsAsync(ct),
            await CarePlansAsync(ct),
            await NotificationsAsync(ct),
            await PrivacyRequestsAsync(ct),
            await AccessLogAsync(relationships, ct));
    }

    private string Tenant(Guid? tenantId) => tenantId is { } id ? _tenants.GetValueOrDefault(id, string.Empty) : string.Empty;

    private string Professional(Guid profileId) => _professionals.GetValueOrDefault(profileId, string.Empty);

    /// <summary>Names of every professional profile that authored one of the subject's records.</summary>
    private async Task<Dictionary<Guid, string>> ProfessionalNamesAsync(IReadOnlyList<CareRelationship> relationships, CancellationToken ct)
    {
        var ids = relationships.Select(r => r.ProfessionalProfileId)
            .Concat(await db.PhysicalAssessments.Where(a => a.ClientId == userId).Select(a => a.AssessorProfileId).ToListAsync(ct))
            .Concat(await db.NutritionAssessments.Where(a => a.ClientId == userId).Select(a => a.DietitianProfileId).ToListAsync(ct))
            .Concat(await db.TrainingPlanVersions.Where(v => v.ClientId == userId).Select(v => v.TrainerProfileId).ToListAsync(ct))
            .Concat(await db.MealPlanVersions.Where(v => v.ClientId == userId).Select(v => v.DietitianProfileId).ToListAsync(ct))
            .Concat(await db.TherapySessions.Where(s => s.ClientId == userId).Select(s => s.PractitionerProfileId).ToListAsync(ct))
            .Distinct()
            .ToList();
        return await db.ProfessionalProfiles.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
    }

    private async Task<IReadOnlyList<ExportProfessionalProfile>> OwnProfessionalProfilesAsync(CancellationToken ct) =>
        await db.ProfessionalProfiles
            .Where(p => p.UserId == userId)
            .Select(p => new ExportProfessionalProfile(p.Id, p.Discipline, p.DisplayName, p.Bio, p.Registration, p.VerificationStatus, p.VerifiedAt, p.CreatedAt))
            .ToListAsync(ct);

    private async Task<IReadOnlyList<ExportMembership>> MembershipsAsync(CancellationToken ct)
    {
        var memberships = await db.TenantMemberships.Where(m => m.UserId == userId).OrderBy(m => m.InvitedAt).ToListAsync(ct);
        return memberships
            .Select(m => new ExportMembership(m.TenantId, Tenant(m.TenantId), m.Disciplines, m.IsAdministrator, m.Status, m.JoinedAt, m.RevokedAt))
            .ToList();
    }

    private async Task<ExportHealthHistory?> HealthHistoryAsync(CancellationToken ct)
    {
        var history = await db.HealthHistories.Include(h => h.Revisions).Include(h => h.Reviews).FirstOrDefaultAsync(h => h.ClientId == userId, ct);
        if (history is null)
        {
            return null;
        }

        var reviewerIds = history.Reviews.Select(r => r.ReviewerProfileId).Distinct().ToList();
        var reviewers = await db.ProfessionalProfiles.Where(p => reviewerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        return new ExportHealthHistory(
            history.Revisions.OrderBy(r => r.Number).Select(HealthHistoryMappingForExport).ToList(),
            history.Reviews.OrderBy(r => r.ReviewedAt)
                .Select(r => new ExportHealthHistoryReview(
                    r.RevisionNumber, Tenant(r.TenantId), reviewers.GetValueOrDefault(r.ReviewerProfileId, string.Empty), r.Discipline, r.Notes, r.ReviewedAt))
                .ToList());
    }

    private static HealthHistoryRevisionResponse HealthHistoryMappingForExport(HealthHistoryRevision r) =>
        new(r.Number, r.Content, r.IsSignificantChange, r.SubmittedAt, r.AcknowledgedAt);

    private async Task<IReadOnlyList<ExportPhysicalAssessment>> PhysicalAssessmentsAsync(CancellationToken ct)
    {
        var rows = await db.PhysicalAssessments.Where(a => a.ClientId == userId).OrderBy(a => a.AssessedOn).ToListAsync(ct);
        return rows.Select(a => new ExportPhysicalAssessment(
            a.Id, Tenant(a.TenantId), Professional(a.AssessorProfileId), a.AssessedOn, a.Measurements, a.Notes, a.Version, a.Corrections, a.CreatedAt)).ToList();
    }

    /// <summary>Published versions only: drafts are the professional's unfinished work, not the client's records.</summary>
    private async Task<IReadOnlyList<ExportTrainingPlanVersion>> TrainingPlansAsync(CancellationToken ct)
    {
        var rows = await db.TrainingPlanVersions.Where(v => v.ClientId == userId).OrderBy(v => v.PublishedAt).ToListAsync(ct);
        return rows.Select(v => new ExportTrainingPlanVersion(
            v.TrainingPlanId, Tenant(v.TenantId), Professional(v.TrainerProfileId), v.VersionNumber, v.Details, v.Content, v.ChangeDescription, v.PublishedAt)).ToList();
    }

    private async Task<IReadOnlyList<ExportWorkoutSession>> WorkoutSessionsAsync(CancellationToken ct)
    {
        var sessions = await db.WorkoutSessions.Include(s => s.Sets).Where(s => s.ClientId == userId).OrderBy(s => s.StartedAt).ToListAsync(ct);
        var heartRates = await db.WorkoutHeartRates.Include(h => h.Samples).Where(h => h.ClientId == userId).ToDictionaryAsync(h => h.Id, ct);

        return sessions.Select(s =>
        {
            var heartRate = heartRates.GetValueOrDefault(s.Id);
            return new ExportWorkoutSession(
                s.Id, Tenant(s.TenantId), s.TrainingPlanId, s.PlanVersionNumber, s.PlannedSessionName, s.Status, s.StartedAt, s.CompletedAt, s.AbandonedAt,
                s.LocalDate,
                s.Sets.OrderBy(x => x.LoggedAt)
                    .Select(x => new ExportSet(x.ExerciseId, x.SetNumber, x.Repetitions, x.LoadKg, x.DurationSeconds, x.DistanceMeters, x.Difficulty, x.Notes,
                        x.LoggedAt, SelfReported, x.Corrections))
                    .ToList(),
                s.Feedback, s.Reply?.Message, s.Reply?.RepliedAt, s.AppleHealthWorkout,
                heartRate is null
                    ? null
                    : new ExportHeartRate(
                        heartRate.IsFinalized, heartRate.Revision, heartRate.Summary,
                        heartRate.Samples.OrderBy(x => x.Timestamp)
                            .Select(x => new ExportHeartRateSample(x.Timestamp, x.BeatsPerMinute, x.SourceName, x.DeviceName, x.Kind, x.ReceivedAt, x.CorrectedAt,
                                x.RemovedAt))
                            .ToList()));
        }).ToList();
    }

    private async Task<ExportAppleHealth> AppleHealthAsync(CancellationToken ct)
    {
        var connection = await db.HealthConnections.Include(c => c.History)
            .FirstOrDefaultAsync(c => c.ClientId == userId && c.Provider == HealthDataProvider.AppleHealth, ct);
        return connection is null
            ? new ExportAppleHealth(HealthConnectionStatus.NotConnected, false, [])
            : new ExportAppleHealth(
                connection.Status, connection.SaveWorkouts,
                connection.History.OrderBy(h => h.ChangedAt).Select(h => new ExportConnectionChange(h.Status, h.ChangedAt)).ToList());
    }

    private async Task<IReadOnlyList<ExportNutritionAssessment>> NutritionAssessmentsAsync(CancellationToken ct)
    {
        var rows = await db.NutritionAssessments.Where(a => a.ClientId == userId).OrderBy(a => a.AssessedOn).ToListAsync(ct);
        return rows.Select(a => new ExportNutritionAssessment(
            a.Id, Tenant(a.TenantId), Professional(a.DietitianProfileId), a.Content, a.Version, a.Corrections, a.CreatedAt)).ToList();
    }

    private async Task<IReadOnlyList<ExportMealPlanVersion>> MealPlansAsync(CancellationToken ct)
    {
        var rows = await db.MealPlanVersions.Where(v => v.ClientId == userId).OrderBy(v => v.PublishedAt).ToListAsync(ct);
        return rows.Select(v => new ExportMealPlanVersion(
            v.MealPlanId, Tenant(v.TenantId), Professional(v.DietitianProfileId), v.VersionNumber, v.Details, v.Content, v.ChangeDescription, v.PublishedAt)).ToList();
    }

    private async Task<IReadOnlyList<ExportFoodDiaryEntry>> FoodDiaryAsync(CancellationToken ct)
    {
        var rows = await db.FoodDiaryEntries.Include(e => e.Comments).Where(e => e.ClientId == userId).OrderBy(e => e.EatenAt).ToListAsync(ct);
        var authorIds = rows.SelectMany(e => e.Comments.Select(c => c.AuthorProfileId)).Distinct().ToList();
        var authors = await db.ProfessionalProfiles.Where(p => authorIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        return rows.Select(e => new ExportFoodDiaryEntry(
                e.Id, e.TenantId is null ? null : Tenant(e.TenantId), e.MealPlanId, e.MealPlanVersionNumber, e.MealName, e.EatenAt, e.LocalDate, e.Description,
                e.WaterMl, e.Adherence, e.ClientNotes,
                e.Comments.OrderBy(c => c.CreatedAt)
                    .Select(c => new ExportDiaryComment(authors.GetValueOrDefault(c.AuthorProfileId, string.Empty), c.Message, c.CreatedAt)).ToList(),
                e.RecordedAt))
            .ToList();
    }

    private async Task<IReadOnlyList<ExportTherapySession>> TherapySessionsAsync(CancellationToken ct)
    {
        var rows = await (
                from s in db.TherapySessions
                join m in db.TherapyModalities on s.ModalityId equals m.Id
                where s.ClientId == userId
                orderby s.StartsAt
                select new { Session = s, m.Code })
            .ToListAsync(ct);
        return rows.Select(r => new ExportTherapySession(
            r.Session.Id, Tenant(r.Session.TenantId), Professional(r.Session.PractitionerProfileId), r.Code, r.Session.ServiceName, r.Session.StartsAt,
            r.Session.Status, r.Session.CompletedAt, r.Session.CancelledAt,
            r.Session.Status == TherapySessionStatus.Completed ? r.Session.Record : null, r.Session.Corrections, r.Session.ClientReport, r.Session.Referrals)).ToList();
    }

    private async Task<IReadOnlyList<ExportTherapyConsent>> TherapyConsentsAsync(CancellationToken ct)
    {
        var rows = await (
                from c in db.TherapyConsents
                join m in db.TherapyModalities on c.ModalityId equals m.Id
                where c.ClientId == userId
                orderby c.DecidedAt
                select new { Consent = c, m.Code })
            .ToListAsync(ct);
        return rows.Select(r => new ExportTherapyConsent(
            Tenant(r.Consent.TenantId), Professional(r.Consent.PractitionerProfileId), r.Code, r.Consent.TemplateVersion, r.Consent.DisclosedRisks,
            r.Consent.Decision, r.Consent.DecidedAt, r.Consent.WithdrawnAt, r.Consent.WithdrawalReason)).ToList();
    }

    private async Task<IReadOnlyList<ExportCarePlan>> CarePlansAsync(CancellationToken ct)
    {
        var rows = await (
                from p in db.TherapyCarePlans
                join m in db.TherapyModalities on p.ModalityId equals m.Id
                where p.ClientId == userId
                orderby p.CreatedAt
                select new { Plan = p, m.Code })
            .ToListAsync(ct);
        return rows.Select(r => new ExportCarePlan(
            Tenant(r.Plan.TenantId), Professional(r.Plan.PractitionerProfileId), r.Code, r.Plan.Goals, r.Plan.PlannedSessions, r.Plan.Frequency, r.Plan.Status,
            r.Plan.Reviews, r.Plan.CreatedAt)).ToList();
    }

    private async Task<IReadOnlyList<ExportNotification>> NotificationsAsync(CancellationToken ct)
    {
        var rows = await db.Notifications.Where(n => n.RecipientUserId == userId).OrderBy(n => n.CreatedAt).ToListAsync(ct);
        return rows.Select(n => new ExportNotification(n.Type, n.TenantId is null ? null : Tenant(n.TenantId), n.CreatedAt, n.ReadAt)).ToList();
    }

    private async Task<IReadOnlyList<ExportPrivacyRequest>> PrivacyRequestsAsync(CancellationToken ct) =>
        await db.PrivacyRequests
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.SubmittedAt)
            .Select(r => new ExportPrivacyRequest(r.Type, r.Status, r.Details, r.SubmittedAt, r.ResolvedAt, r.ResolutionNotes))
            .ToListAsync(ct);

    private async Task<IReadOnlyList<ExportAccessLogEntry>> AccessLogAsync(IReadOnlyList<CareRelationship> relationships, CancellationToken ct)
    {
        var ownProfessionals = relationships
            .GroupBy(r => r.ProfessionalUserId)
            .ToDictionary(g => g.Key, g => Professional(g.First().ProfessionalProfileId));

        var entries = await db.AuditEntries.Where(e => e.SubjectUserId == userId).OrderBy(e => e.OccurredAt).Take(10_000).ToListAsync(ct);
        return entries.Select(e => new ExportAccessLogEntry(
                e.OccurredAt, e.Action, e.ResourceType, e.TenantId is null ? null : Tenant(e.TenantId),
                e.ActorUserId == userId ? "you"
                : ownProfessionals.TryGetValue(e.ActorUserId, out var name) ? $"professional: {name}"
                : e.TenantId is null ? "platform operator"
                : "organization staff"))
            .ToList();
    }
}
