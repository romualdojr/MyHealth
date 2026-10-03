using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;

namespace MyHealth.Infrastructure.Persistence;

/// <summary>
/// Saves all tracked changes atomically. Domain events are dispatched first so that whatever their handlers add
/// (notifications, projections) commits in the same transaction as the change that raised them.
/// </summary>
internal sealed class UnitOfWork(ApplicationDbContext db, IServiceProvider services) : IUnitOfWork
{
    private const int MaxDispatchRounds = 5;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        for (var round = 0; round < MaxDispatchRounds; round++)
        {
            var aggregates = db.ChangeTracker.Entries<IAggregateRoot>()
                .Select(entry => entry.Entity)
                .Where(aggregate => aggregate.DomainEvents.Count > 0)
                .ToList();

            if (aggregates.Count == 0)
            {
                break;
            }

            var events = aggregates.SelectMany(a => a.DomainEvents).ToList();
            aggregates.ForEach(a => a.ClearDomainEvents());

            foreach (var domainEvent in events)
            {
                await DispatchAsync(domainEvent, cancellationToken);
            }
        }

        return await db.SaveChangesAsync(cancellationToken);
    }

    private async Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
        foreach (var handler in services.GetServices(handlerType))
        {
            await (Task)handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.Handle))!
                .Invoke(handler, [domainEvent, cancellationToken])!;
        }
    }
}
