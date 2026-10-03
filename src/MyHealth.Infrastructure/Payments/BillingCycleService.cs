using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Billing;

namespace MyHealth.Infrastructure.Payments;

/// <summary>
/// Runs the billing cycle on an interval (<c>Billing:CycleIntervalMinutes</c>, default daily). The cycle is idempotent,
/// so several instances or a manual run from the back office never issue an invoice twice.
/// </summary>
internal sealed class BillingCycleService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<BillingCycleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(configuration.GetValue("Billing:CycleIntervalMinutes", 24 * 60)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var cycle = scope.ServiceProvider.GetRequiredService<ICommandHandler<RunBillingCycleCommand, BillingCycleResult>>();
                var result = await cycle.Handle(new RunBillingCycleCommand(), stoppingToken);
                if (result.IsSuccess)
                {
                    logger.LogInformation(
                        "Billing cycle: {Subscriptions} subscriptions, {Issued} invoices issued, {PastDue} past due, {Suspended} suspended",
                        result.Value.Subscriptions, result.Value.InvoicesIssued, result.Value.PastDue, result.Value.Suspended);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Billing cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
