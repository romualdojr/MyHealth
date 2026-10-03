using FluentValidation;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Users;

/// <summary>Creates the domain profile for a freshly registered account (onboarding step 1).</summary>
public sealed record CompleteProfileCommand(string FullName, string TimeZoneId) : ICommand<Guid>;

internal sealed class CompleteProfileValidator : AbstractValidator<CompleteProfileCommand>
{
    public CompleteProfileValidator()
    {
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.TimeZoneId).NotEmpty().Must(TimeZones.IsValid).WithMessage("Use an IANA time zone such as America/Sao_Paulo.");
    }
}

internal sealed class CompleteProfileHandler(
    IUserContext user, IUserProfileRepository profiles, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CompleteProfileCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CompleteProfileCommand command, CancellationToken cancellationToken)
    {
        if (await profiles.GetByIdAsync(user.UserId, cancellationToken) is not null)
        {
            return UserErrors.ProfileAlreadyExists;
        }

        var profile = UserProfile.Create(user.UserId, command.FullName, user.Email, command.TimeZoneId, clock.GetUtcNow());
        if (profile.IsFailure)
        {
            return profile.Error;
        }

        profiles.Add(profile.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return profile.Value.Id;
    }
}

public sealed record UpdateProfileCommand(string FullName, string TimeZoneId) : ICommand;

internal sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.TimeZoneId).NotEmpty().Must(TimeZones.IsValid).WithMessage("Use an IANA time zone such as America/Sao_Paulo.");
    }
}

internal sealed class UpdateProfileHandler(IUserContext user, IUserProfileRepository profiles, IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateProfileCommand>
{
    public async Task<Result> Handle(UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetByIdAsync(user.UserId, cancellationToken);
        if (profile is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var result = profile.Update(command.FullName, command.TimeZoneId);
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}
