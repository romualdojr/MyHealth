using Microsoft.AspNetCore.Identity;
using MyHealth.Application.Abstractions.Administration;
using MyHealth.Domain.Abstractions;

namespace MyHealth.Infrastructure.Identity;

/// <summary>Platform-operator role assignments, stored as an Identity role.</summary>
internal sealed class AdministratorDirectory(UserManager<ApplicationUser> users) : IAdministratorDirectory
{
    public async Task<IReadOnlyList<AdministratorItem>> ListAsync(CancellationToken cancellationToken) =>
        (await users.GetUsersInRoleAsync(Roles.Administrator))
        .OrderBy(u => u.Email)
        .Select(u => new AdministratorItem(u.Id, u.Email ?? string.Empty, u.TwoFactorEnabled))
        .ToList();

    public async Task<Result> GrantAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await users.FindByIdAsync(userId.ToString());
        if (account is null)
        {
            return AdministrationErrors.AccountNotFound;
        }

        if (!await users.IsInRoleAsync(account, Roles.Administrator))
        {
            await EnsureAsync(await users.AddToRoleAsync(account, Roles.Administrator));
        }

        return Result.Success();
    }

    public async Task<Result> RevokeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await users.FindByIdAsync(userId.ToString());
        if (account is null)
        {
            return AdministrationErrors.AccountNotFound;
        }

        if (!await users.IsInRoleAsync(account, Roles.Administrator))
        {
            return Result.Success();
        }

        if ((await users.GetUsersInRoleAsync(Roles.Administrator)).Count <= 1)
        {
            return AdministrationErrors.LastAdministrator;
        }

        await EnsureAsync(await users.RemoveFromRoleAsync(account, Roles.Administrator));

        // Existing tokens carry the role claim; rotating the stamp invalidates them at the next refresh.
        await users.UpdateSecurityStampAsync(account);
        return Result.Success();
    }

    private static Task EnsureAsync(IdentityResult result) =>
        result.Succeeded
            ? Task.CompletedTask
            : throw new InvalidOperationException($"Identity role change failed: {string.Join(", ", result.Errors.Select(e => e.Code))}");
}
