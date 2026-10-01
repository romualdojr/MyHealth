namespace MyHealth.Domain.Abstractions;

/// <summary>Something meaningful that happened inside an aggregate. Dispatched in the same transaction.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
