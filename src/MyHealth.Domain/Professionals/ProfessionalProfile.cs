using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Professionals;

/// <summary>
/// A user's professional identity for one discipline. A user may hold one profile per discipline; every
/// professional record references the profile so the role used is always known.
/// </summary>
public sealed class ProfessionalProfile : AggregateRoot<Guid>
{
    private ProfessionalProfile(
        Guid id, Guid userId, Discipline discipline, string displayName, string? bio, string? photoUrl,
        ProfessionalRegistration registration, DateTimeOffset createdAt) : base(id)
    {
        UserId = userId;
        Discipline = discipline;
        DisplayName = displayName;
        Bio = bio;
        PhotoUrl = photoUrl;
        Registration = registration;
        CreatedAt = createdAt;
        VerificationStatus = VerificationStatus.Pending;
    }

    private ProfessionalProfile() { }

    public Guid UserId { get; private set; }

    public Discipline Discipline { get; private set; }

    public string DisplayName { get; private set; } = null!;

    public string? Bio { get; private set; }

    public string? PhotoUrl { get; private set; }

    public ProfessionalRegistration Registration { get; private set; } = null!;

    public VerificationStatus VerificationStatus { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public string? ReviewNotes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsVerified => VerificationStatus == VerificationStatus.Verified;

    public static ProfessionalProfile Create(
        Guid userId, Discipline discipline, string displayName, string? bio, string? photoUrl,
        ProfessionalRegistration registration, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now), userId, discipline, displayName.Trim(), bio, photoUrl, registration, now);

    public void UpdateDetails(string displayName, string? bio, string? photoUrl, ProfessionalRegistration registration)
    {
        DisplayName = displayName.Trim();
        Bio = bio;
        PhotoUrl = photoUrl;

        // A new registration must be checked again before the professional can provide care.
        if (registration != Registration)
        {
            Registration = registration;
            VerificationStatus = VerificationStatus.Pending;
            VerifiedAt = null;
        }
    }

    public Result Verify(Guid adminUserId, string? notes, DateTimeOffset now)
    {
        if (VerificationStatus == VerificationStatus.Verified)
        {
            return ProfessionalErrors.AlreadyVerified;
        }

        VerificationStatus = VerificationStatus.Verified;
        VerifiedAt = now;
        ReviewedBy = adminUserId;
        ReviewNotes = notes;
        Raise(new ProfessionalVerified(Id, UserId, now));
        return Result.Success();
    }

    public Result Reject(Guid adminUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ProfessionalErrors.ReasonRequired;
        }

        VerificationStatus = VerificationStatus.Rejected;
        VerifiedAt = null;
        ReviewedBy = adminUserId;
        ReviewNotes = reason;
        return Result.Success();
    }

    public Result Suspend(Guid adminUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ProfessionalErrors.ReasonRequired;
        }

        VerificationStatus = VerificationStatus.Suspended;
        ReviewedBy = adminUserId;
        ReviewNotes = reason;
        return Result.Success();
    }
}

public sealed record ProfessionalVerified(Guid ProfessionalProfileId, Guid UserId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public static class ProfessionalErrors
{
    public static readonly Error NotFound = Error.NotFound("Professional.NotFound", "The professional profile was not found.");
    public static readonly Error AlreadyExists = Error.Conflict("Professional.AlreadyExists", "You already have a profile for this discipline.");
    public static readonly Error AlreadyVerified = Error.Conflict("Professional.AlreadyVerified", "The professional is already verified.");
    public static readonly Error ReasonRequired = Error.Validation("Professional.ReasonRequired", "A reason is required.");
    public static readonly Error NotVerified = Error.Forbidden("Professional.NotVerified", "Only verified professionals can perform this action.");

    public static Error MissingProfile(Discipline discipline) =>
        Error.Forbidden("Professional.MissingProfile", $"You need a {discipline} profile to perform this action.");
}
