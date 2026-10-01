using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;

namespace MyHealth.Application.Therapies;

public sealed record ModalityResponse(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    IReadOnlyList<string> AcceptedCouncils,
    IReadOnlyList<TherapyRecordField> RequiredRecordFields,
    bool RequiresConsent,
    ModalityStatus Status,
    string? LegalReviewReference,
    DateTimeOffset? EnabledAt,
    int? CurrentConsentVersion);

public sealed record ModalityInput(
    string Name, string? Description, IReadOnlyList<string> AcceptedCouncils, IReadOnlyList<TherapyRecordField> RequiredRecordFields, bool RequiresConsent);

internal sealed class ModalityInputValidator : AbstractValidator<ModalityInput>
{
    public ModalityInputValidator()
    {
        RuleFor(m => m.Name).NotEmpty().MaximumLength(200);
        RuleFor(m => m.Description).MaximumLength(2000);
        RuleFor(m => m.AcceptedCouncils).NotNull().Must(c => c.Count <= 30 && c.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 20))
            .WithMessage("Up to 30 council codes of at most 20 characters.");
        RuleFor(m => m.RequiredRecordFields).NotNull();
        RuleForEach(m => m.RequiredRecordFields).IsInEnum();
    }
}

/// <summary>Platform operators add a modality. It starts disabled until its legal and clinical review (RF-37, §9).</summary>
public sealed record DefineModalityCommand(string Code, ModalityInput Modality) : ICommand<Guid>;

internal sealed class DefineModalityValidator : AbstractValidator<DefineModalityCommand>
{
    public DefineModalityValidator()
    {
        RuleFor(c => c.Code).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Modality).NotNull().SetValidator(new ModalityInputValidator());
    }
}

internal sealed class DefineModalityHandler(ITherapyModalityRepository modalities, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<DefineModalityCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DefineModalityCommand command, CancellationToken cancellationToken)
    {
        if (await modalities.GetByCodeAsync(command.Code.Trim().ToLowerInvariant(), cancellationToken) is not null)
        {
            return TherapyErrors.ModalityExists;
        }

        var m = command.Modality;
        var modality = TherapyModality.Define(command.Code, m.Name, m.Description, m.AcceptedCouncils, m.RequiredRecordFields, m.RequiresConsent, clock.GetUtcNow());
        if (modality.IsFailure)
        {
            return modality.Error;
        }

        modalities.Add(modality.Value);
        audit.Record(AuditActions.Create, nameof(TherapyModality), modality.Value.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return modality.Value.Id;
    }
}

/// <summary>Changes who may practise and what must be documented. On an enabled modality this disables it again for re-review.</summary>
public sealed record ConfigureModalityCommand(string Code, ModalityInput Modality) : ICommand;

internal sealed class ConfigureModalityValidator : AbstractValidator<ConfigureModalityCommand>
{
    public ConfigureModalityValidator() => RuleFor(c => c.Modality).NotNull().SetValidator(new ModalityInputValidator());
}

internal sealed class ConfigureModalityHandler(ITherapyModalityRepository modalities, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ConfigureModalityCommand>
{
    public async Task<Result> Handle(ConfigureModalityCommand command, CancellationToken cancellationToken)
    {
        var modality = await TherapyAccess.ModalityByCodeAsync(modalities, command.Code, cancellationToken);
        if (modality.IsFailure)
        {
            return modality;
        }

        var m = command.Modality;
        modality.Value.Configure(m.Name, m.Description, m.AcceptedCouncils, m.RequiredRecordFields, m.RequiresConsent, clock.GetUtcNow());
        audit.Record(AuditActions.Update, nameof(TherapyModality), modality.Value.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>The legal gate: a modality becomes usable only with a reference to the review that approved it.</summary>
public sealed record ChangeModalityStatusCommand(string Code, bool Enable, string? LegalReviewReference) : ICommand;

internal sealed class ChangeModalityStatusValidator : AbstractValidator<ChangeModalityStatusCommand>
{
    public ChangeModalityStatusValidator() => RuleFor(c => c.LegalReviewReference).MaximumLength(500);
}

internal sealed class ChangeModalityStatusHandler(
    IUserContext user, ITherapyModalityRepository modalities, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ChangeModalityStatusCommand>
{
    public async Task<Result> Handle(ChangeModalityStatusCommand command, CancellationToken cancellationToken)
    {
        var modality = await TherapyAccess.ModalityByCodeAsync(modalities, command.Code, cancellationToken);
        if (modality.IsFailure)
        {
            return modality;
        }

        var now = clock.GetUtcNow();
        Result result;
        if (command.Enable)
        {
            result = modality.Value.Enable(command.LegalReviewReference ?? string.Empty, user.UserId, now);
        }
        else
        {
            modality.Value.Disable(now);
            result = Result.Success();
        }

        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(command.Enable ? AuditActions.Enable : AuditActions.Disable, nameof(TherapyModality), modality.Value.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

public sealed record ConsentTemplateResponse(
    Guid Id, string ModalityCode, int Version, string Title, string Body, IReadOnlyList<string> DisclosedRisks, DateTimeOffset PublishedAt);

/// <summary>Publishes a new consent version (wording and risks from legal counsel). Clients must consent again afterwards.</summary>
public sealed record PublishConsentTemplateCommand(string ModalityCode, string Title, string Body, IReadOnlyList<string> DisclosedRisks) : ICommand<int>;

internal sealed class PublishConsentTemplateValidator : AbstractValidator<PublishConsentTemplateCommand>
{
    public PublishConsentTemplateValidator()
    {
        RuleFor(c => c.Title).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Body).NotEmpty().MaximumLength(20_000);
        RuleFor(c => c.DisclosedRisks).NotNull().Must(r => r.Count is > 0 and <= 50 && r.All(x => x.Length <= 1000))
            .WithMessage("Disclose between 1 and 50 risks of at most 1000 characters each.");
    }
}

internal sealed class PublishConsentTemplateHandler(
    IUserContext user, ITherapyModalityRepository modalities, IConsentTemplateRepository templates, IAuditLog audit, IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<PublishConsentTemplateCommand, int>
{
    public async Task<Result<int>> Handle(PublishConsentTemplateCommand command, CancellationToken cancellationToken)
    {
        var modality = await TherapyAccess.ModalityByCodeAsync(modalities, command.ModalityCode, cancellationToken);
        if (modality.IsFailure)
        {
            return modality.Error;
        }

        var current = await templates.GetCurrentAsync(modality.Value.Id, cancellationToken);
        var template = ConsentTemplate.Publish(
            modality.Value, current?.Version ?? 0, command.Title, command.Body, command.DisclosedRisks, user.UserId, clock.GetUtcNow());
        if (template.IsFailure)
        {
            return template.Error;
        }

        templates.Add(template.Value);
        audit.Record(AuditActions.Publish, nameof(ConsentTemplate), template.Value.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return template.Value.Version;
    }
}

/// <summary>Modalities: everyone sees enabled ones (the catalogue the apps show); operators can include disabled ones.</summary>
public sealed record ListModalitiesQuery(bool IncludeDisabled) : IQuery<IReadOnlyList<ModalityResponse>>;

internal sealed class ListModalitiesHandler(IReadDbContext db) : IQueryHandler<ListModalitiesQuery, IReadOnlyList<ModalityResponse>>
{
    public async Task<Result<IReadOnlyList<ModalityResponse>>> Handle(ListModalitiesQuery query, CancellationToken cancellationToken)
    {
        var modalities = db.TherapyModalities.AsQueryable();
        if (!query.IncludeDisabled)
        {
            modalities = modalities.Where(m => m.Status == ModalityStatus.Enabled);
        }

        var rows = await modalities.OrderBy(m => m.Name).ToListAsync(cancellationToken);
        var ids = rows.Select(m => m.Id).ToList();
        var versions = await db.ConsentTemplates
            .Where(t => ids.Contains(t.ModalityId))
            .GroupBy(t => t.ModalityId)
            .Select(g => new { ModalityId = g.Key, Version = g.Max(t => t.Version) })
            .ToDictionaryAsync(x => x.ModalityId, x => x.Version, cancellationToken);

        return rows.Select(m => new ModalityResponse(
                m.Id, m.Code, m.Name, m.Description, m.AcceptedCouncils, m.RequiredRecordFields, m.RequiresConsent, m.Status, m.LegalReviewReference, m.EnabledAt,
                versions.TryGetValue(m.Id, out var v) ? v : null))
            .ToList();
    }
}

public sealed record QualificationResponse(
    Guid Id,
    Guid ProfessionalProfileId,
    string PractitionerName,
    string Council,
    string RegistrationNumber,
    string Region,
    string ModalityCode,
    string Title,
    string Issuer,
    string? CertificateNumber,
    DateOnly? IssuedOn,
    QualificationStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string? ReviewNotes);

/// <summary>The operators' review queue of practitioner qualifications (RF-33). Professional data only.</summary>
public sealed record ListQualificationsQuery(QualificationStatus? Status) : IQuery<IReadOnlyList<QualificationResponse>>;

internal sealed class ListQualificationsHandler(IReadDbContext db) : IQueryHandler<ListQualificationsQuery, IReadOnlyList<QualificationResponse>>
{
    public async Task<Result<IReadOnlyList<QualificationResponse>>> Handle(ListQualificationsQuery query, CancellationToken cancellationToken)
    {
        var qualifications = db.PractitionerQualifications.AsQueryable();
        if (query.Status is { } status)
        {
            qualifications = qualifications.Where(q => q.Status == status);
        }

        return Result.Success(await QualificationMapping.ListAsync(db, qualifications, cancellationToken));
    }
}

public enum QualificationDecision
{
    Verify = 1,
    Reject = 2,
    Revoke = 3,
}

/// <summary>Verifies, rejects or revokes a qualification. Verification checks the council is within the modality's scope.</summary>
public sealed record DecideQualificationCommand(Guid QualificationId, QualificationDecision Decision, string? Notes) : ICommand;

internal sealed class DecideQualificationValidator : AbstractValidator<DecideQualificationCommand>
{
    public DecideQualificationValidator()
    {
        RuleFor(c => c.Decision).IsInEnum();
        RuleFor(c => c.Notes).MaximumLength(1000);
    }
}

internal sealed class DecideQualificationHandler(
    IUserContext user, IPractitionerQualificationRepository qualifications, IProfessionalProfileRepository professionals, ITherapyModalityRepository modalities,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<DecideQualificationCommand>
{
    public async Task<Result> Handle(DecideQualificationCommand command, CancellationToken cancellationToken)
    {
        var qualification = await qualifications.GetByIdAsync(command.QualificationId, cancellationToken);
        if (qualification is null)
        {
            return QualificationErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        Result result;
        switch (command.Decision)
        {
            case QualificationDecision.Verify:
                var practitioner = await professionals.GetByIdAsync(qualification.ProfessionalProfileId, cancellationToken);
                var modality = await modalities.GetByIdAsync(qualification.ModalityId, cancellationToken);
                result = qualification.Verify(practitioner!, modality!, user.UserId, command.Notes, now);
                break;
            case QualificationDecision.Reject:
                result = qualification.Reject(user.UserId, command.Notes ?? string.Empty, now);
                break;
            default:
                result = qualification.Revoke(user.UserId, command.Notes ?? string.Empty, now);
                break;
        }

        if (result.IsFailure)
        {
            return result;
        }

        var action = command.Decision switch
        {
            QualificationDecision.Verify => AuditActions.Verify,
            QualificationDecision.Reject => AuditActions.Reject,
            _ => AuditActions.Revoke,
        };
        audit.Record(action, nameof(PractitionerQualification), qualification.Id, qualification.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

internal static class QualificationMapping
{
    public static async Task<IReadOnlyList<QualificationResponse>> ListAsync(
        IReadDbContext db, IQueryable<PractitionerQualification> qualifications, CancellationToken cancellationToken)
    {
        var rows = await (
                from q in qualifications
                join p in db.ProfessionalProfiles on q.ProfessionalProfileId equals p.Id
                join m in db.TherapyModalities on q.ModalityId equals m.Id
                orderby q.SubmittedAt
                select new { Qualification = q, p.DisplayName, p.Registration, m.Code })
            .Take(500)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new QualificationResponse(
                r.Qualification.Id, r.Qualification.ProfessionalProfileId, r.DisplayName, r.Registration.Council, r.Registration.Number, r.Registration.Region, r.Code,
                r.Qualification.Title, r.Qualification.Issuer, r.Qualification.CertificateNumber, r.Qualification.IssuedOn, r.Qualification.Status,
                r.Qualification.SubmittedAt, r.Qualification.ReviewedAt, r.Qualification.ReviewNotes))
            .ToList();
    }
}
