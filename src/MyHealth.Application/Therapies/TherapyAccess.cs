using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;

namespace MyHealth.Application.Therapies;

/// <summary>The practitioner acting in the selected tenant, with the modality in question and their qualification for it.</summary>
internal sealed record ActingPractitioner(ActingProfessional Professional, TherapyModality Modality, PractitionerQualification? Qualification)
{
    public ProfessionalProfile Profile => Professional.Profile;

    public Guid TenantId => Professional.TenantId;
}

internal static class TherapyAccess
{
    /// <summary>
    /// The caller as a therapy practitioner in the selected tenant (membership with the discipline, own profile, MFA),
    /// plus the modality and their current qualification. Care rules (verified, enabled, in scope, qualified) are left to
    /// the domain so that every write re-checks them.
    /// </summary>
    public static async Task<Result<ActingPractitioner>> ForModalityAsync(
        ICareAccess access, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications, Guid modalityId,
        CancellationToken cancellationToken)
    {
        var professional = await access.GetCurrentProfessionalAsync(Discipline.TherapyPractitioner, requireVerified: false, cancellationToken);
        if (professional.IsFailure)
        {
            return professional.Error;
        }

        var modality = await modalities.GetByIdAsync(modalityId, cancellationToken);
        if (modality is null)
        {
            return TherapyErrors.ModalityNotFound;
        }

        var qualification = await qualifications.GetCurrentAsync(professional.Value.Profile.Id, modality.Id, cancellationToken);
        return new ActingPractitioner(professional.Value, modality, qualification);
    }

    public static async Task<Result<TherapyModality>> ModalityByCodeAsync(ITherapyModalityRepository modalities, string code, CancellationToken cancellationToken) =>
        await modalities.GetByCodeAsync(code.Trim().ToLowerInvariant(), cancellationToken) is { } modality ? modality : TherapyErrors.ModalityNotFound;
}
