using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;

namespace MyHealth.Application.Therapies;

/// <summary>The consent text and risks the client must read before deciding (RF-35).</summary>
public sealed record GetCurrentConsentTemplateQuery(string ModalityCode) : IQuery<ConsentTemplateResponse>;

internal sealed class GetCurrentConsentTemplateHandler(IReadDbContext db) : IQueryHandler<GetCurrentConsentTemplateQuery, ConsentTemplateResponse>
{
    public async Task<Result<ConsentTemplateResponse>> Handle(GetCurrentConsentTemplateQuery query, CancellationToken cancellationToken)
    {
        var code = query.ModalityCode.Trim().ToLowerInvariant();
        var template = await (
                from t in db.ConsentTemplates
                join m in db.TherapyModalities on t.ModalityId equals m.Id
                where m.Code == code
                orderby t.Version descending
                select new ConsentTemplateResponse(t.Id, m.Code, t.Version, t.Title, t.Body, t.DisclosedRisks, t.PublishedAt))
            .FirstOrDefaultAsync(cancellationToken);

        return template is null ? ConsentErrors.NoTemplate : template;
    }
}

public sealed record ConsentResponse(
    Guid Id,
    Guid TenantId,
    string OrganizationName,
    string ModalityCode,
    Guid PractitionerProfileId,
    string PractitionerName,
    int TemplateVersion,
    IReadOnlyList<string> DisclosedRisks,
    ConsentDecision Decision,
    DateTimeOffset DecidedAt,
    DateTimeOffset? WithdrawnAt,
    string? WithdrawalReason,
    bool InForce);

/// <summary>
/// The client grants or refuses consent for a procedure with one practitioner (RF-35). The decision is on the version the
/// client read: if a newer one was published meanwhile, the client must read that one. Refusals are recorded as well.
/// </summary>
public sealed record DecideConsentCommand(Guid RelationshipId, string ModalityCode, int TemplateVersion, ConsentDecision Decision) : ICommand<Guid>;

internal sealed class DecideConsentValidator : AbstractValidator<DecideConsentCommand>
{
    public DecideConsentValidator()
    {
        RuleFor(c => c.ModalityCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.TemplateVersion).GreaterThan(0);
        RuleFor(c => c.Decision).IsInEnum();
    }
}

internal sealed class DecideConsentHandler(
    IUserContext user, ICareRelationshipRepository relationships, IProfessionalProfileRepository professionals, ITherapyModalityRepository modalities,
    IConsentTemplateRepository templates, ITherapyConsentRepository consents, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<DecideConsentCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DecideConsentCommand command, CancellationToken cancellationToken)
    {
        var relationship = await relationships.GetByIdAsync(command.RelationshipId, cancellationToken);
        if (relationship is null || relationship.ClientId != user.UserId || relationship.Discipline != Discipline.TherapyPractitioner)
        {
            return CareRelationshipErrors.NotFound;
        }

        if (!relationship.IsActive)
        {
            return CareRelationshipErrors.NotActive;
        }

        var modality = await TherapyAccess.ModalityByCodeAsync(modalities, command.ModalityCode, cancellationToken);
        if (modality.IsFailure)
        {
            return modality.Error;
        }

        if (!modality.Value.IsEnabled)
        {
            return TherapyErrors.ModalityDisabled;
        }

        var template = await templates.GetCurrentAsync(modality.Value.Id, cancellationToken);
        if (template is null)
        {
            return ConsentErrors.NoTemplate;
        }

        if (template.Version != command.TemplateVersion)
        {
            return ConsentErrors.OutdatedVersion;
        }

        var practitioner = await professionals.GetByIdAsync(relationship.ProfessionalProfileId, cancellationToken);
        var consent = TherapyConsent.Decide(user.UserId, relationship.TenantId, practitioner!, template, command.Decision, clock.GetUtcNow());
        consents.Add(consent);
        audit.Record(AuditActions.Create, $"TherapyConsent.{command.Decision}", consent.Id, user.UserId, relationship.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return consent.Id;
    }
}

/// <summary>The client withdraws a consent; sessions can no longer be completed under it. Past sessions keep their record.</summary>
public sealed record WithdrawConsentCommand(Guid ConsentId, string? Reason) : ICommand;

internal sealed class WithdrawConsentValidator : AbstractValidator<WithdrawConsentCommand>
{
    public WithdrawConsentValidator() => RuleFor(c => c.Reason).MaximumLength(1000);
}

internal sealed class WithdrawConsentHandler(IUserContext user, ITherapyConsentRepository consents, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<WithdrawConsentCommand>
{
    public async Task<Result> Handle(WithdrawConsentCommand command, CancellationToken cancellationToken)
    {
        var consent = await consents.GetByIdAsync(command.ConsentId, cancellationToken);
        if (consent is null || consent.ClientId != user.UserId)
        {
            return ConsentErrors.NotFound;
        }

        var result = consent.Withdraw(user.UserId, command.Reason, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Withdraw, nameof(TherapyConsent), consent.Id, user.UserId, consent.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record ListMyConsentsQuery : IQuery<IReadOnlyList<ConsentResponse>>;

internal sealed class ListMyConsentsHandler(IUserContext user, IReadDbContext db) : IQueryHandler<ListMyConsentsQuery, IReadOnlyList<ConsentResponse>>
{
    public async Task<Result<IReadOnlyList<ConsentResponse>>> Handle(ListMyConsentsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await ConsentMapping.ListAsync(db, db.TherapyConsents.Where(c => c.ClientId == user.UserId), cancellationToken));
}

/// <summary>The practitioner sees the client's consent decisions given to them in the active tenant. Audited.</summary>
public sealed record ListClientConsentsQuery(Guid ClientId) : IQuery<IReadOnlyList<ConsentResponse>>;

internal sealed class ListClientConsentsHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListClientConsentsQuery, IReadOnlyList<ConsentResponse>>
{
    public async Task<Result<IReadOnlyList<ConsentResponse>>> Handle(ListClientConsentsQuery query, CancellationToken cancellationToken)
    {
        var practitioner = await access.GetCurrentProfessionalAsync(Discipline.TherapyPractitioner, requireVerified: false, cancellationToken);
        if (practitioner.IsFailure)
        {
            return practitioner.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(practitioner.Value, query.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var items = await ConsentMapping.ListAsync(
            db,
            db.TherapyConsents.Where(c => c.ClientId == query.ClientId && c.TenantId == practitioner.Value.TenantId && c.PractitionerProfileId == practitioner.Value.Profile.Id),
            cancellationToken);
        await audit.RecordReadAsync("TherapyConsent.List", relationship.Value.Id, query.ClientId, practitioner.Value.TenantId, cancellationToken);
        return Result.Success(items);
    }
}

internal static class ConsentMapping
{
    public static async Task<IReadOnlyList<ConsentResponse>> ListAsync(IReadDbContext db, IQueryable<TherapyConsent> consents, CancellationToken cancellationToken)
    {
        var rows = await (
                from c in consents
                join t in db.Tenants on c.TenantId equals t.Id
                join m in db.TherapyModalities on c.ModalityId equals m.Id
                join p in db.ProfessionalProfiles on c.PractitionerProfileId equals p.Id
                orderby c.DecidedAt descending
                select new { Consent = c, Organization = t.Name, m.Code, Practitioner = p.DisplayName })
            .Take(500)
            .ToListAsync(cancellationToken);

        var modalityIds = rows.Select(r => r.Consent.ModalityId).Distinct().ToList();
        var current = await db.ConsentTemplates
            .Where(t => modalityIds.Contains(t.ModalityId))
            .GroupBy(t => t.ModalityId)
            .Select(g => new { ModalityId = g.Key, Version = g.Max(t => t.Version) })
            .ToDictionaryAsync(x => x.ModalityId, x => x.Version, cancellationToken);

        return rows.Select(r =>
        {
            var c = r.Consent;
            var inForce = current.TryGetValue(c.ModalityId, out var version) && c.IsValidFor(c.PractitionerProfileId, c.ModalityId, version);
            return new ConsentResponse(
                c.Id, c.TenantId, r.Organization, r.Code, c.PractitionerProfileId, r.Practitioner, c.TemplateVersion, c.DisclosedRisks, c.Decision, c.DecidedAt,
                c.WithdrawnAt, c.WithdrawalReason, inForce);
        }).ToList();
    }
}
