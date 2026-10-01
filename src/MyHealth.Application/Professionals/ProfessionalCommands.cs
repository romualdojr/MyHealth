using FluentValidation;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Professionals;

public sealed record RegistrationInput(string Council, string Number, string Region);

public sealed record CreateProfessionalProfileCommand(
    Discipline Discipline, string DisplayName, string? Bio, string? PhotoUrl, RegistrationInput Registration) : ICommand<Guid>;

internal sealed class RegistrationInputValidator : AbstractValidator<RegistrationInput>
{
    public RegistrationInputValidator()
    {
        RuleFor(r => r.Council).NotEmpty().MaximumLength(20);
        RuleFor(r => r.Number).NotEmpty().MaximumLength(30);
        RuleFor(r => r.Region).NotEmpty().MaximumLength(10);
    }
}

internal sealed class CreateProfessionalProfileValidator : AbstractValidator<CreateProfessionalProfileCommand>
{
    public CreateProfessionalProfileValidator()
    {
        RuleFor(c => c.Discipline).IsInEnum();
        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Bio).MaximumLength(2000);
        RuleFor(c => c.PhotoUrl).MaximumLength(2048);
        RuleFor(c => c.Registration).NotNull().SetValidator(new RegistrationInputValidator());
    }
}

internal sealed class CreateProfessionalProfileHandler(
    IUserContext user, IUserProfileRepository users, IProfessionalProfileRepository professionals, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateProfessionalProfileCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateProfessionalProfileCommand command, CancellationToken cancellationToken)
    {
        if (await users.GetByIdAsync(user.UserId, cancellationToken) is null)
        {
            return UserErrors.ProfileNotFound;
        }

        if (await professionals.GetByUserAsync(user.UserId, command.Discipline, cancellationToken) is not null)
        {
            return ProfessionalErrors.AlreadyExists;
        }

        var registration = ProfessionalRegistration.Create(command.Registration.Council, command.Registration.Number, command.Registration.Region);
        var profile = ProfessionalProfile.Create(
            user.UserId, command.Discipline, command.DisplayName, command.Bio, command.PhotoUrl, registration, clock.GetUtcNow());

        professionals.Add(profile);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return profile.Id;
    }
}

public sealed record UpdateProfessionalProfileCommand(
    Guid ProfileId, string DisplayName, string? Bio, string? PhotoUrl, RegistrationInput Registration) : ICommand;

internal sealed class UpdateProfessionalProfileValidator : AbstractValidator<UpdateProfessionalProfileCommand>
{
    public UpdateProfessionalProfileValidator()
    {
        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Bio).MaximumLength(2000);
        RuleFor(c => c.PhotoUrl).MaximumLength(2048);
        RuleFor(c => c.Registration).NotNull().SetValidator(new RegistrationInputValidator());
    }
}

internal sealed class UpdateProfessionalProfileHandler(
    IUserContext user, IProfessionalProfileRepository professionals, IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateProfessionalProfileCommand>
{
    public async Task<Result> Handle(UpdateProfessionalProfileCommand command, CancellationToken cancellationToken)
    {
        var profile = await professionals.GetByIdAsync(command.ProfileId, cancellationToken);
        if (profile is null || profile.UserId != user.UserId)
        {
            return ProfessionalErrors.NotFound;
        }

        var registration = ProfessionalRegistration.Create(command.Registration.Council, command.Registration.Number, command.Registration.Region);
        profile.UpdateDetails(command.DisplayName, command.Bio, command.PhotoUrl, registration);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record VerifyProfessionalCommand(Guid ProfileId, string? Notes) : ICommand;

internal sealed class VerifyProfessionalHandler(
    IUserContext user, IProfessionalProfileRepository professionals, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<VerifyProfessionalCommand>
{
    public async Task<Result> Handle(VerifyProfessionalCommand command, CancellationToken cancellationToken)
    {
        var profile = await professionals.GetByIdAsync(command.ProfileId, cancellationToken);
        if (profile is null)
        {
            return ProfessionalErrors.NotFound;
        }

        var result = profile.Verify(user.UserId, command.Notes, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Verify, nameof(ProfessionalProfile), profile.Id, profile.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

public sealed record RejectProfessionalCommand(Guid ProfileId, string Reason) : ICommand;

internal sealed class RejectProfessionalValidator : AbstractValidator<RejectProfessionalCommand>
{
    public RejectProfessionalValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
}

internal sealed class RejectProfessionalHandler(
    IUserContext user, IProfessionalProfileRepository professionals, IAuditLog audit, IUnitOfWork unitOfWork)
    : ICommandHandler<RejectProfessionalCommand>
{
    public async Task<Result> Handle(RejectProfessionalCommand command, CancellationToken cancellationToken)
    {
        var profile = await professionals.GetByIdAsync(command.ProfileId, cancellationToken);
        if (profile is null)
        {
            return ProfessionalErrors.NotFound;
        }

        var result = profile.Reject(user.UserId, command.Reason);
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Reject, nameof(ProfessionalProfile), profile.Id, profile.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
