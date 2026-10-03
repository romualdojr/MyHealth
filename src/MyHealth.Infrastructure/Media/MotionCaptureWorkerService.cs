using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyHealth.Application.MotionCapture;

namespace MyHealth.Infrastructure.Media;

/// <summary>Runs the 3D conversion processor on an interval (<c>MotionCapture:WorkerIntervalSeconds</c>, default 15).</summary>
internal sealed class MotionCaptureWorkerService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<MotionCaptureWorkerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("MotionCapture:WorkerIntervalSeconds", 15)));
        do
        {
            try
            {
                int handled;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    handled = await scope.ServiceProvider.GetRequiredService<MotionCaptureProcessor>().ProcessDueAsync(stoppingToken);
                }
                while (handled == MotionCaptureProcessor.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Motion capture round failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
