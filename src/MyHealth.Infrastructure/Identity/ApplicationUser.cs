using Microsoft.AspNetCore.Identity;

namespace MyHealth.Infrastructure.Identity;

/// <summary>Credentials only. Everything the business cares about lives in the domain <c>UserProfile</c>.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() => Id = Guid.CreateVersion7();
}

public static class Roles
{
    public const string Administrator = "Administrator";
}
