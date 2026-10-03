using MyHealth.Domain.Abstractions;

namespace MyHealth.Application.Abstractions.Messaging;

/// <summary>A request that changes state and returns no data.</summary>
public interface ICommand;

/// <summary>A request that changes state and returns <typeparamref name="TResponse"/> (typically an id).</summary>
public interface ICommand<TResponse>;

/// <summary>A read-only request.</summary>
public interface IQuery<TResponse>;

public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<Result> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Reacts to a domain event inside the same unit of work that raised it.</summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}
