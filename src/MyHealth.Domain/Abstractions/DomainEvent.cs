namespace MyHealth.Domain.Abstractions;

public abstract record DomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;
