using MyHealth.Domain.Abstractions;

namespace MyHealth.Application.Abstractions.Administration;

public sealed record AdministratorItem(Guid UserId, string Email, bool TwoFactorEnabled);

/// <summary>Who holds the platform-operator role. Kept behind an interface because roles live in the identity store.</summary>
public interface IAdministratorDirectory
{
    Task<IReadOnlyList<AdministratorItem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Fails with <see cref="AdministrationErrors.AccountNotFound"/> when no account has this id.</summary>
    Task<Result> GrantAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Refuses to remove the last operator.</summary>
    Task<Result> RevokeAsync(Guid userId, CancellationToken cancellationToken);
}

public static class AdministrationErrors
{
    public static readonly Error AccountNotFound = Error.NotFound("Administration.AccountNotFound", "The account was not found.");
    public static readonly Error LastAdministrator = Error.Conflict(
        "Administration.LastAdministrator", "The platform needs at least one administrator; appoint another one first.");
    public static readonly Error CannotRevokeSelf = Error.Conflict(
        "Administration.CannotRevokeSelf", "Ask another administrator to remove your administrator role.");
}
