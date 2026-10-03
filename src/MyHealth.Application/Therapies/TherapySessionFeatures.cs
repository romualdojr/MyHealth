using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;

namespace MyHealth.Application.Therapies;

public sealed record TherapyRecordInput(
    string? ReportedConcern,
    string? ProfessionalAssessment,
    string? ContraindicationsAndPrecautions,
    string? Technique,
    string? PointsOrRegions,
    int? DurationMinutes,
    string? AdverseEvents,
    string? PostSessionGuidance)
{
    internal TherapyRecord ToRecord() =>
        new(ReportedConcern, ProfessionalAssessment, ContraindicationsAndPrecautions, Technique, PointsOrRegions, DurationMinutes, AdverseEvents, PostSessionGuidance);
}

internal sealed class TherapyRecordInputValidator : AbstractValidator<TherapyRecordInput>
{
    public TherapyRecordInputValidator()
    {
        RuleFor(r => r.ReportedConcern).MaximumLength(4000);
        RuleFor(r => r.ProfessionalAssessment).MaximumLength(8000);
        RuleFor(r => r.ContraindicationsAndPrecautions).MaximumLength(4000);
        RuleFor(r => r.Technique).MaximumLength(4000);
        RuleFor(r => r.PointsOrRegions).MaximumLength(4000);
        RuleFor(r => r.DurationMinutes).InclusiveBetween(1, 480);
        RuleFor(r => r.AdverseEvents).MaximumLength(4000);
        RuleFor(r => r.PostSessionGuidance).MaximumLength(4000);
    }
}

public sealed record TherapySessionResponse(
    Guid Id,
    Guid TenantId,
    string OrganizationName,
    Guid ClientId,
    Guid PractitionerProfileId,
    string PractitionerName,
    Guid ServiceId,
    string ServiceName,
    string ModalityCode,
    string ModalityName,
    ServiceDelivery Delivery,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    TherapySessionStatus Status,
    DateTimeOffset? CancelledAt,
    bool CancelledByClient,
    string? CancellationReason,
    TherapyRecord? Record,
    DateTimeOffset? RecordUpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<TherapyRecordField> MissingRequiredFields,
    bool ConsentRequired,
    bool ConsentInForce,
    IReadOnlyList<TherapyRecordCorrection> Corrections,
    ClientProgressReport? ClientReport,
    IReadOnlyList<MedicalReferral> Referrals);

/// <summary>
/// The practitioner books a session of one of their services for a client linked to them in the active tenant (RF-36).
/// Needs an enabled modality and a verified qualification; overlapping sessions of the practitioner are refused.
/// </summary>
public sealed record ScheduleTherapySessionCommand(Guid ClientId, Guid ServiceId, DateTimeOffset StartsAt) : ICommand<Guid>;

internal sealed class ScheduleTherapySessionValidator : AbstractValidator<ScheduleTherapySessionCommand>
{
    public ScheduleTherapySessionValidator()
    {
        RuleFor(c => c.ClientId).NotEmpty();
        RuleFor(c => c.ServiceId).NotEmpty();
    }
}

internal sealed class ScheduleTherapySessionHandler(
    ICareAccess access, ITherapyServiceRepository services, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications,
    ITherapySessionRepository sessions, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ScheduleTherapySessionCommand, Guid>
{
    public async Task<Result<Guid>> Handle(ScheduleTherapySessionCommand command, CancellationToken cancellationToken)
    {
        var practitioner = await access.GetCurrentProfessionalAsync(Discipline.TherapyPractitioner, requireVerified: true, cancellationToken);
        if (practitioner.IsFailure)
        {
            return practitioner.Error;
        }

        var service = await services.GetByIdAsync(command.ServiceId, cancellationToken);
        if (service is null || service.PractitionerProfileId != practitioner.Value.Profile.Id || service.TenantId != practitioner.Value.TenantId)
        {
            if (service is not null)
            {
                await audit.RecordDeniedAsync(nameof(TherapyService), service.Id, practitioner.Value.TenantId, cancellationToken);
            }

            return TherapyServiceErrors.NotFound;
        }

        var relationship = await access.GetActiveRelationshipAsync(practitioner.Value, command.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var acting = await TherapyAccess.ForModalityAsync(access, modalities, qualifications, service.ModalityId, cancellationToken);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var a = acting.Value;
        var session = TherapySession.Schedule(command.ClientId, a.Profile, a.Modality, a.Qualification, service, command.StartsAt, clock.GetUtcNow());
        if (session.IsFailure)
        {
            return session.Error;
        }

        if (await sessions.HasOverlapAsync(a.Profile.Id, session.Value.StartsAt, session.Value.EndsAt, null, cancellationToken))
        {
            return TherapySessionErrors.Overlap;
        }

        sessions.Add(session.Value);
        audit.Record(AuditActions.Schedule, nameof(TherapySession), session.Value.Id, command.ClientId, session.Value.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return session.Value.Id;
    }
}

public sealed record RescheduleTherapySessionCommand(Guid SessionId, DateTimeOffset StartsAt) : ICommand;

internal sealed class RescheduleTherapySessionHandler(
    ICareAccess access, ITherapySessionRepository sessions, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RescheduleTherapySessionCommand>
{
    public async Task<Result> Handle(RescheduleTherapySessionCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TherapySessionLoader.LoadLedAsync(access, sessions, audit, command.SessionId, requireVerified: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var (practitioner, session) = loaded.Value;
        var eligible = await TherapySessionLoader.EnsureEligibleAsync(access, modalities, qualifications, session.ModalityId, cancellationToken);
        if (eligible.IsFailure)
        {
            return eligible;
        }

        var duration = session.EndsAt - session.StartsAt;
        if (await sessions.HasOverlapAsync(practitioner.Profile.Id, command.StartsAt, command.StartsAt + duration, session.Id, cancellationToken))
        {
            return TherapySessionErrors.Overlap;
        }

        var result = session.Reschedule(practitioner.Profile.Id, command.StartsAt, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Schedule, nameof(TherapySession), session.Id, session.ClientId, session.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>The practitioner cancels a scheduled session; the client is notified.</summary>
public sealed record CancelTherapySessionCommand(Guid SessionId, string? Reason) : ICommand;

internal sealed class CancelTherapySessionValidator : AbstractValidator<CancelTherapySessionCommand>
{
    public CancelTherapySessionValidator() => RuleFor(c => c.Reason).MaximumLength(1000);
}

internal sealed class CancelTherapySessionHandler(
    ICareAccess access, ITherapySessionRepository sessions, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CancelTherapySessionCommand>
{
    public async Task<Result> Handle(CancelTherapySessionCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TherapySessionLoader.LoadLedAsync(access, sessions, audit, command.SessionId, requireVerified: false, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var (practitioner, session) = loaded.Value;
        var result = session.Cancel(practitioner.Profile.UserId, command.Reason, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Cancel, nameof(TherapySession), session.Id, session.ClientId, session.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>Saves the care record of a scheduled session (RF-34). Only a verified, qualified practitioner documents procedures.</summary>
public sealed record DocumentTherapySessionCommand(Guid SessionId, TherapyRecordInput Record) : ICommand;

internal sealed class DocumentTherapySessionValidator : AbstractValidator<DocumentTherapySessionCommand>
{
    public DocumentTherapySessionValidator() => RuleFor(c => c.Record).NotNull().SetValidator(new TherapyRecordInputValidator());
}

internal sealed class DocumentTherapySessionHandler(
    ICareAccess access, ITherapySessionRepository sessions, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<DocumentTherapySessionCommand>
{
    public async Task<Result> Handle(DocumentTherapySessionCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TherapySessionLoader.LoadLedAsync(access, sessions, audit, command.SessionId, requireVerified: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var (_, session) = loaded.Value;
        var eligible = await TherapySessionLoader.EnsureEligibleAsync(access, modalities, qualifications, session.ModalityId, cancellationToken);
        if (eligible.IsFailure)
        {
            return eligible;
        }

        var result = session.Document(session.PractitionerProfileId, command.Record.ToRecord(), clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Update, nameof(TherapySession), session.Id, session.ClientId, session.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>
/// Completes a session (acceptance test 8): refused while mandatory fields are empty or, where the modality requires
/// it, without a consent in force on the current consent version. Eligibility is checked again at this moment.
/// </summary>
public sealed record CompleteTherapySessionCommand(Guid SessionId) : ICommand;

internal sealed class CompleteTherapySessionHandler(
    ICareAccess access, ITherapySessionRepository sessions, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications,
    ITherapyConsentRepository consents, IConsentTemplateRepository templates, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CompleteTherapySessionCommand>
{
    public async Task<Result> Handle(CompleteTherapySessionCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TherapySessionLoader.LoadLedAsync(access, sessions, audit, command.SessionId, requireVerified: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var (_, session) = loaded.Value;
        var acting = await TherapyAccess.ForModalityAsync(access, modalities, qualifications, session.ModalityId, cancellationToken);
        if (acting.IsFailure)
        {
            return acting;
        }

        var relationship = await access.GetActiveRelationshipAsync(acting.Value.Professional, session.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship;
        }

        var a = acting.Value;
        var consent = await consents.GetLatestAsync(session.ClientId, session.TenantId, session.PractitionerProfileId, session.ModalityId, cancellationToken);
        var template = await templates.GetCurrentAsync(session.ModalityId, cancellationToken);
        var result = session.Complete(a.Profile, a.Modality, a.Qualification, consent, template, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Complete, nameof(TherapySession), session.Id, session.ClientId, session.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>Corrects a completed record with a reason; the replaced record is kept (§5 History).</summary>
public sealed record CorrectTherapyRecordCommand(Guid SessionId, TherapyRecordInput Record, string Reason) : ICommand;

internal sealed class CorrectTherapyRecordValidator : AbstractValidator<CorrectTherapyRecordCommand>
{
    public CorrectTherapyRecordValidator()
    {
        RuleFor(c => c.Record).NotNull().SetValidator(new TherapyRecordInputValidator());
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
    }
}

internal sealed class CorrectTherapyRecordHandler(
    ICareAccess access, ITherapySessionRepository sessions, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CorrectTherapyRecordCommand>
{
    public async Task<Result> Handle(CorrectTherapyRecordCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TherapySessionLoader.LoadLedAsync(access, sessions, audit, command.SessionId, requireVerified: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var (_, session) = loaded.Value;
        var modality = await modalities.GetByIdAsync(session.ModalityId, cancellationToken);
        var eligible = await TherapySessionLoader.EnsureEligibleAsync(access, modalities, qualifications, session.ModalityId, cancellationToken);
        if (eligible.IsFailure)
        {
            return eligible;
        }

        var result = session.CorrectRecord(session.PractitionerProfileId, modality!, command.Record.ToRecord(), command.Reason, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Correct, nameof(TherapySession), session.Id, session.ClientId, session.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>The practitioner advises the client to seek medical care because of warning signs (RF-36). The client is notified.</summary>
public sealed record RecommendMedicalCareCommand(Guid SessionId, string Reason, ReferralUrgency Urgency) : ICommand;

internal sealed class RecommendMedicalCareValidator : AbstractValidator<RecommendMedicalCareCommand>
{
    public RecommendMedicalCareValidator()
    {
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(c => c.Urgency).IsInEnum();
    }
}

internal sealed class RecommendMedicalCareHandler(
    ICareAccess access, ITherapySessionRepository sessions, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RecommendMedicalCareCommand>
{
    public async Task<Result> Handle(RecommendMedicalCareCommand command, CancellationToken cancellationToken)
    {
        // Advising medical care is never blocked by credential paperwork: any practitioner leading the session may do it.
        var loaded = await TherapySessionLoader.LoadLedAsync(access, sessions, audit, command.SessionId, requireVerified: false, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        var (_, session) = loaded.Value;
        var result = session.RecommendMedicalCare(session.PractitionerProfileId, command.Reason, command.Urgency, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Refer, nameof(TherapySession), session.Id, session.ClientId, session.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>The practitioner's sessions in the active tenant, optionally for one client and period (their schedule).</summary>
public sealed record ListPractitionerSessionsQuery(Guid? ClientId, DateTimeOffset? From, DateTimeOffset? To) : IQuery<IReadOnlyList<TherapySessionResponse>>;

internal sealed class ListPractitionerSessionsHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListPractitionerSessionsQuery, IReadOnlyList<TherapySessionResponse>>
{
    public async Task<Result<IReadOnlyList<TherapySessionResponse>>> Handle(ListPractitionerSessionsQuery query, CancellationToken cancellationToken)
    {
        var practitioner = await access.GetCurrentProfessionalAsync(Discipline.TherapyPractitioner, requireVerified: false, cancellationToken);
        if (practitioner.IsFailure)
        {
            return practitioner.Error;
        }

        var p = practitioner.Value;
        var activeClients = db.CareRelationships
            .Where(r => r.TenantId == p.TenantId && r.ProfessionalProfileId == p.Profile.Id && r.Status == RelationshipStatus.Active)
            .Select(r => r.ClientId);
        var sessions = db.TherapySessions.Where(s => s.TenantId == p.TenantId && s.PractitionerProfileId == p.Profile.Id && activeClients.Contains(s.ClientId));
        if (query.ClientId is { } clientId)
        {
            sessions = sessions.Where(s => s.ClientId == clientId);
        }

        if (query.From is { } from)
        {
            sessions = sessions.Where(s => s.StartsAt >= from);
        }

        if (query.To is { } to)
        {
            sessions = sessions.Where(s => s.StartsAt <= to);
        }

        var items = await TherapySessionMapping.ListAsync(db, sessions, forClient: false, cancellationToken);
        await audit.RecordReadAsync("TherapySession.List", p.Profile.Id, query.ClientId, p.TenantId, cancellationToken);
        return Result.Success(items);
    }
}

/// <summary>
/// One session: readable by its client, or by its practitioner while acting for its tenant with an active relationship
/// (audited). Anyone else gets "not found", and the attempt is audited (MT-09).
/// </summary>
public sealed record GetTherapySessionQuery(Guid SessionId) : IQuery<TherapySessionResponse>;

internal sealed class GetTherapySessionHandler(IReadDbContext db, IUserContext user, ITenantContext tenant, IAuditLog audit)
    : IQueryHandler<GetTherapySessionQuery, TherapySessionResponse>
{
    public async Task<Result<TherapySessionResponse>> Handle(GetTherapySessionQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var session = await db.TherapySessions.FirstOrDefaultAsync(s => s.Id == query.SessionId, cancellationToken);
        if (session is null)
        {
            return TherapySessionErrors.NotFound;
        }

        var single = db.TherapySessions.Where(s => s.Id == session.Id);
        if (session.ClientId == user.UserId)
        {
            return (await TherapySessionMapping.ListAsync(db, single, forClient: true, cancellationToken))[0];
        }

        var activeTenantId = membership.Value?.TenantId;
        var allowed = session.PractitionerUserId == user.UserId
                      && session.TenantId == activeTenantId
                      && membership.Value!.Practices(Discipline.TherapyPractitioner)
                      && await db.ProfessionalProfiles.AnyAsync(
                          p => p.Id == session.PractitionerProfileId
                               && p.VerificationStatus != VerificationStatus.Suspended && p.VerificationStatus != VerificationStatus.Rejected,
                          cancellationToken)
                      && await db.CareRelationships.AnyAsync(
                          r => r.TenantId == session.TenantId && r.ProfessionalProfileId == session.PractitionerProfileId && r.ClientId == session.ClientId
                               && r.Status == RelationshipStatus.Active,
                          cancellationToken);

        if (!allowed)
        {
            await audit.RecordDeniedAsync(nameof(TherapySession), session.Id, activeTenantId, cancellationToken);
            return TherapySessionErrors.NotFound;
        }

        await audit.RecordReadAsync(nameof(TherapySession), session.Id, session.ClientId, session.TenantId, cancellationToken);
        return (await TherapySessionMapping.ListAsync(db, single, forClient: false, cancellationToken))[0];
    }
}

/// <summary>The client's appointments, records and guidance from every organization (journey 6, MT-10).</summary>
public sealed record ListMyTherapySessionsQuery : IQuery<IReadOnlyList<TherapySessionResponse>>;

internal sealed class ListMyTherapySessionsHandler(IUserContext user, IReadDbContext db)
    : IQueryHandler<ListMyTherapySessionsQuery, IReadOnlyList<TherapySessionResponse>>
{
    public async Task<Result<IReadOnlyList<TherapySessionResponse>>> Handle(ListMyTherapySessionsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await TherapySessionMapping.ListAsync(db, db.TherapySessions.Where(s => s.ClientId == user.UserId), forClient: true, cancellationToken));
}

/// <summary>The client cancels one of their scheduled sessions; the practitioner is notified.</summary>
public sealed record CancelMyTherapySessionCommand(Guid SessionId, string? Reason) : ICommand;

internal sealed class CancelMyTherapySessionValidator : AbstractValidator<CancelMyTherapySessionCommand>
{
    public CancelMyTherapySessionValidator() => RuleFor(c => c.Reason).MaximumLength(1000);
}

internal sealed class CancelMyTherapySessionHandler(IUserContext user, ITherapySessionRepository sessions, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CancelMyTherapySessionCommand>
{
    public async Task<Result> Handle(CancelMyTherapySessionCommand command, CancellationToken cancellationToken)
    {
        var session = await sessions.GetByIdAsync(command.SessionId, cancellationToken);
        if (session is null || session.ClientId != user.UserId)
        {
            return TherapySessionErrors.NotFound;
        }

        var result = session.Cancel(user.UserId, command.Reason, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Cancel, nameof(TherapySession), session.Id, user.UserId, session.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>The client reports how they feel after a session (journey 6, RF-36); the practitioner is notified.</summary>
public sealed record ReportTherapyProgressCommand(Guid SessionId, int Wellbeing, string? Comments) : ICommand;

internal sealed class ReportTherapyProgressValidator : AbstractValidator<ReportTherapyProgressCommand>
{
    public ReportTherapyProgressValidator()
    {
        RuleFor(c => c.Wellbeing).InclusiveBetween(1, 5);
        RuleFor(c => c.Comments).MaximumLength(2000);
    }
}

internal sealed class ReportTherapyProgressHandler(IUserContext user, ITherapySessionRepository sessions, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ReportTherapyProgressCommand>
{
    public async Task<Result> Handle(ReportTherapyProgressCommand command, CancellationToken cancellationToken)
    {
        var session = await sessions.GetByIdAsync(command.SessionId, cancellationToken);
        if (session is null || session.ClientId != user.UserId)
        {
            return TherapySessionErrors.NotFound;
        }

        var result = session.ReportProgress(user.UserId, command.Wellbeing, command.Comments, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

internal static class TherapySessionLoader
{
    /// <summary>
    /// Loads a session only if the caller leads it in the active tenant; otherwise "not found", with a <c>deny</c> audit
    /// for a known id (MT-09).
    /// </summary>
    public static async Task<Result<(ActingProfessional Practitioner, TherapySession Session)>> LoadLedAsync(
        ICareAccess access, ITherapySessionRepository sessions, IAuditLog audit, Guid sessionId, bool requireVerified, CancellationToken cancellationToken)
    {
        var practitioner = await access.GetCurrentProfessionalAsync(Discipline.TherapyPractitioner, requireVerified, cancellationToken);
        if (practitioner.IsFailure)
        {
            return practitioner.Error;
        }

        var session = await sessions.GetByIdAsync(sessionId, cancellationToken);
        if (session is null || !session.IsLedBy(practitioner.Value.Profile.Id) || session.TenantId != practitioner.Value.TenantId)
        {
            if (session is not null)
            {
                await audit.RecordDeniedAsync(nameof(TherapySession), session.Id, practitioner.Value.TenantId, cancellationToken);
            }

            return TherapySessionErrors.NotFound;
        }

        var relationship = await access.GetActiveRelationshipAsync(practitioner.Value, session.ClientId, cancellationToken);
        return relationship.IsFailure ? relationship.Error : (practitioner.Value, session);
    }

    /// <summary>Re-checks that the practitioner may still document this modality (acceptance test 7, therapy part).</summary>
    public static async Task<Result> EnsureEligibleAsync(
        ICareAccess access, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications, Guid modalityId,
        CancellationToken cancellationToken)
    {
        var acting = await TherapyAccess.ForModalityAsync(access, modalities, qualifications, modalityId, cancellationToken);
        return acting.IsFailure ? acting : TherapyRules.EnsureCanProvideCare(acting.Value.Profile, acting.Value.Modality, acting.Value.Qualification);
    }
}

internal static class TherapySessionMapping
{
    /// <summary>
    /// Clients see the record once the session is completed: until then it is the practitioner's unfinished work, saved over
    /// several edits (like plan drafts, it is not shown or exported).
    /// </summary>
    public static async Task<IReadOnlyList<TherapySessionResponse>> ListAsync(
        IReadDbContext db, IQueryable<TherapySession> sessions, bool forClient, CancellationToken cancellationToken)
    {
        var rows = await (
                from s in sessions
                join t in db.Tenants on s.TenantId equals t.Id
                join m in db.TherapyModalities on s.ModalityId equals m.Id
                join p in db.ProfessionalProfiles on s.PractitionerProfileId equals p.Id
                orderby s.StartsAt descending
                select new { Session = s, Organization = t.Name, Modality = m, Practitioner = p.DisplayName })
            .Take(500)
            .ToListAsync(cancellationToken);

        var modalityIds = rows.Select(r => r.Session.ModalityId).Distinct().ToList();
        var clientIds = rows.Select(r => r.Session.ClientId).Distinct().ToList();
        var versions = await db.ConsentTemplates
            .Where(t => modalityIds.Contains(t.ModalityId))
            .GroupBy(t => t.ModalityId)
            .Select(g => new { ModalityId = g.Key, Version = g.Max(t => t.Version) })
            .ToDictionaryAsync(x => x.ModalityId, x => x.Version, cancellationToken);
        var consents = await db.TherapyConsents
            .Where(c => clientIds.Contains(c.ClientId) && modalityIds.Contains(c.ModalityId))
            .ToListAsync(cancellationToken);

        return rows.Select(r =>
        {
            var s = r.Session;
            var latest = consents
                .Where(c => c.ClientId == s.ClientId && c.TenantId == s.TenantId && c.PractitionerProfileId == s.PractitionerProfileId && c.ModalityId == s.ModalityId)
                .MaxBy(c => c.DecidedAt);
            var inForce = latest is not null && versions.TryGetValue(s.ModalityId, out var version) && latest.IsValidFor(s.PractitionerProfileId, s.ModalityId, version);
            return new TherapySessionResponse(
                s.Id, s.TenantId, r.Organization, s.ClientId, s.PractitionerProfileId, r.Practitioner, s.ServiceId, s.ServiceName, r.Modality.Code, r.Modality.Name,
                s.Delivery, s.StartsAt, s.EndsAt, s.Status, s.CancelledAt, s.CancelledBy == s.ClientId, s.CancellationReason,
                forClient && s.Status != TherapySessionStatus.Completed ? null : s.Record, s.RecordUpdatedAt,
                s.CompletedAt, s.Status == TherapySessionStatus.Completed ? [] : r.Modality.MissingFields(s.Record), r.Modality.RequiresConsent, inForce,
                s.Corrections, s.ClientReport, s.Referrals);
        }).ToList();
    }
}
