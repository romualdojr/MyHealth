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

/// <summary>
/// A practitioner submits their training for a modality for verification (RF-33). It belongs to the person, so no
/// tenant is needed, and it can be submitted before the modality is enabled.
/// </summary>
public sealed record SubmitQualificationCommand(string ModalityCode, string Title, string Issuer, string? CertificateNumber, DateOnly? IssuedOn) : ICommand<Guid>;

internal sealed class SubmitQualificationValidator : AbstractValidator<SubmitQualificationCommand>
{
    public SubmitQualificationValidator()
    {
        RuleFor(c => c.ModalityCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Title).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Issuer).NotEmpty().MaximumLength(200);
        RuleFor(c => c.CertificateNumber).MaximumLength(100);
    }
}

internal sealed class SubmitQualificationHandler(
    IUserContext user, IProfessionalProfileRepository professionals, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications,
    IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SubmitQualificationCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SubmitQualificationCommand command, CancellationToken cancellationToken)
    {
        var practitioner = await professionals.GetByUserAsync(user.UserId, Discipline.TherapyPractitioner, cancellationToken);
        if (practitioner is null)
        {
            return ProfessionalErrors.MissingProfile(Discipline.TherapyPractitioner);
        }

        var modality = await TherapyAccess.ModalityByCodeAsync(modalities, command.ModalityCode, cancellationToken);
        if (modality.IsFailure)
        {
            return modality.Error;
        }

        if (await qualifications.GetCurrentAsync(practitioner.Id, modality.Value.Id, cancellationToken) is { Status: QualificationStatus.Pending or QualificationStatus.Verified })
        {
            return QualificationErrors.AlreadySubmitted;
        }

        var qualification = PractitionerQualification.Submit(
            practitioner, modality.Value, command.Title, command.Issuer, command.CertificateNumber, command.IssuedOn, clock.GetUtcNow());
        if (qualification.IsFailure)
        {
            return qualification.Error;
        }

        qualifications.Add(qualification.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return qualification.Value.Id;
    }
}

public sealed record ListMyQualificationsQuery : IQuery<IReadOnlyList<QualificationResponse>>;

internal sealed class ListMyQualificationsHandler(IUserContext user, IReadDbContext db) : IQueryHandler<ListMyQualificationsQuery, IReadOnlyList<QualificationResponse>>
{
    public async Task<Result<IReadOnlyList<QualificationResponse>>> Handle(ListMyQualificationsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await QualificationMapping.ListAsync(db, db.PractitionerQualifications.Where(q => q.UserId == user.UserId), cancellationToken));
}

public sealed record TherapyServiceInput(string Name, string? Description, int DurationMinutes, ServiceDelivery Delivery);

internal sealed class TherapyServiceInputValidator : AbstractValidator<TherapyServiceInput>
{
    public TherapyServiceInputValidator()
    {
        RuleFor(s => s.Name).NotEmpty().MaximumLength(200);
        RuleFor(s => s.Description).MaximumLength(2000);
        RuleFor(s => s.DurationMinutes).InclusiveBetween(5, 480);
        RuleFor(s => s.Delivery).IsInEnum();
    }
}

public sealed record TherapyServiceResponse(
    Guid Id,
    Guid TenantId,
    string OrganizationName,
    string ModalityCode,
    string ModalityName,
    Guid PractitionerProfileId,
    string PractitionerName,
    string Name,
    string? Description,
    int DurationMinutes,
    ServiceDelivery Delivery,
    bool IsActive);

/// <summary>
/// A qualified practitioner adds a service to the active tenant's catalogue (RF-33): only for an enabled modality, with a
/// verified profile, a council within its scope and a verified qualification.
/// </summary>
public sealed record OfferTherapyServiceCommand(string ModalityCode, TherapyServiceInput Service) : ICommand<Guid>;

internal sealed class OfferTherapyServiceValidator : AbstractValidator<OfferTherapyServiceCommand>
{
    public OfferTherapyServiceValidator()
    {
        RuleFor(c => c.ModalityCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Service).NotNull().SetValidator(new TherapyServiceInputValidator());
    }
}

internal sealed class OfferTherapyServiceHandler(
    ICareAccess access, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications, ITherapyServiceRepository services,
    IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<OfferTherapyServiceCommand, Guid>
{
    public async Task<Result<Guid>> Handle(OfferTherapyServiceCommand command, CancellationToken cancellationToken)
    {
        var modality = await TherapyAccess.ModalityByCodeAsync(modalities, command.ModalityCode, cancellationToken);
        if (modality.IsFailure)
        {
            return modality.Error;
        }

        var acting = await TherapyAccess.ForModalityAsync(access, modalities, qualifications, modality.Value.Id, cancellationToken);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var a = acting.Value;
        var s = command.Service;
        var service = TherapyService.Offer(a.TenantId, a.Profile, a.Modality, a.Qualification, s.Name, s.Description, s.DurationMinutes, s.Delivery, clock.GetUtcNow());
        if (service.IsFailure)
        {
            return service.Error;
        }

        services.Add(service.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return service.Value.Id;
    }
}

public sealed record UpdateTherapyServiceCommand(Guid ServiceId, TherapyServiceInput Service, bool IsActive) : ICommand;

internal sealed class UpdateTherapyServiceValidator : AbstractValidator<UpdateTherapyServiceCommand>
{
    public UpdateTherapyServiceValidator() => RuleFor(c => c.Service).NotNull().SetValidator(new TherapyServiceInputValidator());
}

internal sealed class UpdateTherapyServiceHandler(
    ICareAccess access, ITherapyServiceRepository services, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<UpdateTherapyServiceCommand>
{
    public async Task<Result> Handle(UpdateTherapyServiceCommand command, CancellationToken cancellationToken)
    {
        var practitioner = await access.GetCurrentProfessionalAsync(Discipline.TherapyPractitioner, requireVerified: true, cancellationToken);
        if (practitioner.IsFailure)
        {
            return practitioner;
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

        // Offering the service again is offering care again: the practitioner must still be eligible.
        if (command.IsActive && !service.IsActive)
        {
            var eligible = await TherapySessionLoader.EnsureEligibleAsync(access, modalities, qualifications, service.ModalityId, cancellationToken);
            if (eligible.IsFailure)
            {
                return eligible;
            }
        }

        var s = command.Service;
        var result = service.Update(s.Name, s.Description, s.DurationMinutes, s.Delivery, command.IsActive, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>The active tenant's therapy catalogue, by modality and practitioner. Readable by any member; contains no client data.</summary>
public sealed record ListTenantTherapyServicesQuery : IQuery<IReadOnlyList<TherapyServiceResponse>>;

internal sealed class ListTenantTherapyServicesHandler(ITenantContext tenant, IReadDbContext db)
    : IQueryHandler<ListTenantTherapyServicesQuery, IReadOnlyList<TherapyServiceResponse>>
{
    public async Task<Result<IReadOnlyList<TherapyServiceResponse>>> Handle(ListTenantTherapyServicesQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.RequireAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        return Result.Success(await TherapyServiceMapping.ListAsync(db, db.TherapyServices.Where(s => s.TenantId == membership.Value.TenantId), cancellationToken));
    }
}

/// <summary>Active services of the practitioners the client is linked to, in every organization (MT-10).</summary>
public sealed record ListMyTherapyServicesQuery : IQuery<IReadOnlyList<TherapyServiceResponse>>;

internal sealed class ListMyTherapyServicesHandler(IUserContext user, IReadDbContext db)
    : IQueryHandler<ListMyTherapyServicesQuery, IReadOnlyList<TherapyServiceResponse>>
{
    public async Task<Result<IReadOnlyList<TherapyServiceResponse>>> Handle(ListMyTherapyServicesQuery query, CancellationToken cancellationToken)
    {
        var mine = db.TherapyServices.Where(s => s.IsActive && db.CareRelationships.Any(
            r => r.ClientId == user.UserId && r.Status == RelationshipStatus.Active && r.TenantId == s.TenantId && r.ProfessionalProfileId == s.PractitionerProfileId));
        var items = await TherapyServiceMapping.ListAsync(db, mine, cancellationToken);
        return Result.Success<IReadOnlyList<TherapyServiceResponse>>(items.Where(s => s.IsActive).ToList());
    }
}

internal static class TherapyServiceMapping
{
    public static async Task<IReadOnlyList<TherapyServiceResponse>> ListAsync(IReadDbContext db, IQueryable<TherapyService> services, CancellationToken cancellationToken) =>
        await (
                from s in services
                join t in db.Tenants on s.TenantId equals t.Id
                join m in db.TherapyModalities on s.ModalityId equals m.Id
                join p in db.ProfessionalProfiles on s.PractitionerProfileId equals p.Id
                orderby m.Name, p.DisplayName, s.Name
                select new TherapyServiceResponse(
                    s.Id, s.TenantId, t.Name, m.Code, m.Name, s.PractitionerProfileId, p.DisplayName, s.Name, s.Description, s.DurationMinutes, s.Delivery,
                    s.IsActive && m.Status == ModalityStatus.Enabled))
            .ToListAsync(cancellationToken);
}
