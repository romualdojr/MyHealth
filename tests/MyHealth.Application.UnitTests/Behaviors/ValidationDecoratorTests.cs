using FluentValidation;
using MyHealth.Application.Abstractions.Behaviors;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Users;
using MyHealth.Domain.Abstractions;

namespace MyHealth.Application.UnitTests.Behaviors;

public sealed class ValidationDecoratorTests
{
    [Fact]
    public async Task Invalid_command_never_reaches_the_handler()
    {
        var inner = Substitute.For<ICommandHandler<CompleteProfileCommand, Guid>>();
        var decorator = new ValidationDecorator.CommandHandler<CompleteProfileCommand, Guid>(inner, [new CompleteProfileValidator()]);

        var result = await decorator.Handle(new CompleteProfileCommand("", "Not/AZone"), TestContext.Current.CancellationToken);

        var error = result.Error.ShouldBeOfType<ValidationError>();
        error.Errors.Keys.ShouldBe(["FullName", "TimeZoneId"], ignoreOrder: true);
        await inner.DidNotReceiveWithAnyArgs().Handle(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Valid_command_is_handled()
    {
        var id = Guid.CreateVersion7();
        var inner = Substitute.For<ICommandHandler<CompleteProfileCommand, Guid>>();
        inner.Handle(Arg.Any<CompleteProfileCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(id));
        IValidator<CompleteProfileCommand>[] validators = [new CompleteProfileValidator()];
        var decorator = new ValidationDecorator.CommandHandler<CompleteProfileCommand, Guid>(inner, validators);

        var result = await decorator.Handle(new CompleteProfileCommand("Ana", "America/Sao_Paulo"), TestContext.Current.CancellationToken);

        result.Value.ShouldBe(id);
    }
}
