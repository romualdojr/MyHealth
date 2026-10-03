using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Therapies;

public enum ServiceDelivery
{
    InPerson = 1,
    Remote = 2,
}

/// <summary>
/// One entry of a tenant's therapy catalogue: a modality offered by a qualified practitioner (RF-33). It can only be
/// created for an enabled modality by a verified practitioner with a verified qualification for it.
/// </summary>
public sealed class TherapyService : AggregateRoot<Guid>
{
    private TherapyService(Guid id) : base(id) { }

    private TherapyService() { }

    public Guid TenantId { get; private set; }

    public Guid ModalityId { get; private set; }

    public Guid PractitionerProfileId { get; private set; }

    public Guid PractitionerUserId { get; private set; }

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    public int DurationMinutes { get; private set; }

    public ServiceDelivery Delivery { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<TherapyService> Offer(
        Guid tenantId, ProfessionalProfile practitioner, TherapyModality modality, PractitionerQualification? qualification, string name, string? description,
        int durationMinutes, ServiceDelivery delivery, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A therapy service always belongs to a tenant.", nameof(tenantId));
        }

        var eligible = TherapyRules.EnsureCanProvideCare(practitioner, modality, qualification);
        if (eligible.IsFailure)
        {
            return eligible.Error;
        }

        var valid = ValidateDuration(durationMinutes);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        return new TherapyService(Guid.CreateVersion7(now))
        {
            TenantId = tenantId,
            ModalityId = modality.Id,
            PractitionerProfileId = practitioner.Id,
            PractitionerUserId = practitioner.UserId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            DurationMinutes = durationMinutes,
            Delivery = delivery,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public Result Update(string name, string? description, int durationMinutes, ServiceDelivery delivery, bool isActive, DateTimeOffset now)
    {
        var valid = ValidateDuration(durationMinutes);
        if (valid.IsFailure)
        {
            return valid;
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DurationMinutes = durationMinutes;
        Delivery = delivery;
        IsActive = isActive;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Result ValidateDuration(int minutes) =>
        minutes is < 5 or > 480 ? TherapySessionErrors.InvalidDuration : Result.Success();
}

public interface ITherapyServiceRepository : IRepository<TherapyService, Guid>;

public static class TherapyServiceErrors
{
    public static readonly Error NotFound = Error.NotFound("TherapyService.NotFound", "The therapy service was not found.");
    public static readonly Error Inactive = Error.Conflict("TherapyService.Inactive", "The therapy service is no longer offered.");
}

public static class TherapyRules
{
    /// <summary>
    /// "Verify qualifications and permitted scope for each modality before enabling appointments or care records" (RF-33):
    /// a verified practitioner profile, an enabled modality, a council within its scope and a verified qualification.
    /// </summary>
    public static Result EnsureCanProvideCare(ProfessionalProfile practitioner, TherapyModality modality, PractitionerQualification? qualification)
    {
        if (practitioner.Discipline != Discipline.TherapyPractitioner)
        {
            return ProfessionalErrors.MissingProfile(Discipline.TherapyPractitioner);
        }

        if (!practitioner.IsVerified)
        {
            return ProfessionalErrors.NotVerified;
        }

        if (!modality.IsEnabled)
        {
            return TherapyErrors.ModalityDisabled;
        }

        if (!modality.AcceptsRegistration(practitioner.Registration))
        {
            return TherapyErrors.CouncilNotAccepted;
        }

        return qualification is { IsVerified: true } && qualification.ProfessionalProfileId == practitioner.Id && qualification.ModalityId == modality.Id
            ? Result.Success()
            : TherapyErrors.NotQualified;
    }
}
