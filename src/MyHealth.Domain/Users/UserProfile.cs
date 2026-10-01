using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Users;

/// <summary>
/// The person behind an account. Its id is the identity-provider user id, so credentials stay outside the domain.
/// </summary>
public sealed class UserProfile : AggregateRoot<Guid>
{
    private UserProfile(Guid id, string fullName, string email, string timeZoneId, DateTimeOffset createdAt) : base(id)
    {
        FullName = fullName;
        Email = email;
        TimeZoneId = timeZoneId;
        CreatedAt = createdAt;
    }

    private UserProfile() { }

    public string FullName { get; private set; } = null!;

    /// <summary>Normalized (lower-case) e-mail, used to match care invitations.</summary>
    public string Email { get; private set; } = null!;

    /// <summary>IANA time zone, required to count attendance on the client's local date.</summary>
    public string TimeZoneId { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<UserProfile> Create(Guid id, string fullName, string email, string timeZoneId, DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            return UserErrors.InvalidId;
        }

        if (!TimeZones.IsValid(timeZoneId))
        {
            return UserErrors.InvalidTimeZone;
        }

        return new UserProfile(id, fullName.Trim(), NormalizeEmail(email), timeZoneId, now);
    }

    public Result Update(string fullName, string timeZoneId)
    {
        if (!TimeZones.IsValid(timeZoneId))
        {
            return UserErrors.InvalidTimeZone;
        }

        FullName = fullName.Trim();
        TimeZoneId = timeZoneId;
        return Result.Success();
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

public static class UserErrors
{
    public static readonly Error InvalidId = Error.Validation("User.InvalidId", "The user id is required.");
    public static readonly Error InvalidTimeZone = Error.Validation("User.InvalidTimeZone", "The time zone must be a valid IANA time zone id.");
    public static readonly Error ProfileNotFound = Error.NotFound("User.ProfileNotFound", "Complete your profile before using this feature.");
    public static readonly Error ProfileAlreadyExists = Error.Conflict("User.ProfileAlreadyExists", "The profile has already been created.");
}
