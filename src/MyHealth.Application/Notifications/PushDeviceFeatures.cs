using FluentValidation;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Notifications;

namespace MyHealth.Application.Notifications;

public static class PushDeviceErrors
{
    public static readonly Error InstallationInUse = Error.Conflict(
        "PushDevice.InstallationInUse", "This installation is registered to another account; register from the device with its current push token.");
}

/// <summary>
/// The app registers (or refreshes) its push token after sign-in. Idempotent per installation: signing in as someone
/// else on the same phone moves the device to the new user, which requires presenting that phone's push token.
/// </summary>
public sealed record RegisterPushDeviceCommand(Guid InstallationId, PushPlatform Platform, string Token) : ICommand;

internal sealed class RegisterPushDeviceValidator : AbstractValidator<RegisterPushDeviceCommand>
{
    public RegisterPushDeviceValidator()
    {
        RuleFor(c => c.InstallationId).NotEmpty();
        RuleFor(c => c.Platform).IsInEnum();
        RuleFor(c => c.Token).NotEmpty().MaximumLength(4096);
    }
}

internal sealed class RegisterPushDeviceHandler(IUserContext user, IPushDeviceRepository devices, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RegisterPushDeviceCommand>
{
    public async Task<Result> Handle(RegisterPushDeviceCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var device = await devices.GetByInstallationAsync(command.InstallationId, cancellationToken);
        if (device is null)
        {
            devices.Add(PushDevice.Register(command.InstallationId, user.UserId, command.Platform, command.Token, now));
        }
        else if (device.UserId == user.UserId || device.Token == command.Token.Trim())
        {
            // Someone else may take the installation over only from the phone itself, which proves it with the same token.
            device.Update(user.UserId, command.Platform, command.Token, now);
        }
        else
        {
            return PushDeviceErrors.InstallationInUse;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>Sign-out on a phone: it stops receiving pushes. Unknown or someone else's installation is a no-op.</summary>
public sealed record UnregisterPushDeviceCommand(Guid InstallationId) : ICommand;

internal sealed class UnregisterPushDeviceHandler(IUserContext user, IPushDeviceRepository devices, IUnitOfWork unitOfWork)
    : ICommandHandler<UnregisterPushDeviceCommand>
{
    public async Task<Result> Handle(UnregisterPushDeviceCommand command, CancellationToken cancellationToken)
    {
        var device = await devices.GetByInstallationAsync(command.InstallationId, cancellationToken);
        if (device is not null && device.UserId == user.UserId)
        {
            devices.Remove(device);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
