using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Media;
using MyHealth.Application.Abstractions.MotionCapture;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.MotionCapture;

namespace MyHealth.Application.MotionCapture;

/// <summary>
/// Moves 3D conversions forward: sends queued videos to the provider, polls running jobs, copies finished results to
/// MyHealth's media storage and deletes the source video. Each job is claimed with a lease first, so several API
/// instances can run this side by side. Run by a hosted service in the API; tests call <see cref="ProcessDueAsync"/>.
/// </summary>
public sealed partial class MotionCaptureProcessor(
    IMotionCaptureJobRepository jobs, IMotionCaptureProvider provider, IMediaStorage storage, MotionCaptureSettings settings, IUnitOfWork unitOfWork,
    TimeProvider clock, ILogger<MotionCaptureProcessor> logger)
{
    public const int BatchSize = 10;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    /// <summary>Handles the jobs that are due and returns how many it looked at.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var due = await jobs.ListDueAsync(clock.GetUtcNow(), BatchSize, cancellationToken);
        var handled = 0;
        foreach (var jobId in due)
        {
            var now = clock.GetUtcNow();
            if (!await jobs.TryClaimAsync(jobId, now, now + Lease, cancellationToken))
            {
                continue;
            }

            var job = await jobs.GetCurrentAsync(jobId, cancellationToken);
            if (job is null || job.IsFinished)
            {
                continue;
            }

            try
            {
                await StepAsync(job, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
            {
                // The type only: provider messages can echo signed URLs.
                LogTransient(logger, job.Id, ex.GetType().Name);
                job.RecordTransientError(clock.GetUtcNow());
            }

            if (job.IsFinished)
            {
                await DeleteSourceAsync(job, cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            handled++;
        }

        return handled;
    }

    private async Task StepAsync(MotionCaptureJob job, CancellationToken cancellationToken)
    {
        if (job.Status == MotionCaptureStatus.Queued)
        {
            if (job.SourceMediaKey is not { } key)
            {
                job.Fail("O vídeo enviado não está mais disponível. Envie novamente.", clock.GetUtcNow());
                return;
            }

            var source = new MotionCaptureSource(job.SourceFileName, job.SourceContentType, job.SourceSizeBytes, async ct =>
                (await storage.OpenAsync(key, ct))?.Content ?? throw new IOException("Source video missing from storage."));
            var providerJobId = await provider.SubmitAsync(source, cancellationToken);
            job.MarkSubmitted(providerJobId, clock.GetUtcNow(), settings.PollInterval);
            return;
        }

        var status = await provider.GetStatusAsync(job.ProviderJobId!, cancellationToken);
        switch (status.State)
        {
            case MotionCaptureProviderState.Running:
                job.UpdateProgress(status.ProgressPercent, clock.GetUtcNow(), settings.PollInterval);
                return;
            case MotionCaptureProviderState.Failed:
                job.Fail(status.Error ?? "Não foi possível gerar o modelo 3D a partir deste vídeo. Grave de novo com o corpo inteiro visível e boa luz.", clock.GetUtcNow());
                return;
        }

        var outputs = new List<MotionCaptureOutput>();
        foreach (var output in await provider.GetOutputsAsync(job.ProviderJobId!, cancellationToken))
        {
            var kind = output.Format switch
            {
                "mp4" => MediaKind.Loop,
                "glb" or "fbx" => MediaKind.Model3D,
                _ => (MediaKind?)null,
            };
            if (kind is null)
            {
                continue;
            }

            var name = output.Angle is { } angle ? $"{angle.ToString().ToLowerInvariant()}.{output.Format}" : $"model.{output.Format}";
            var key = $"{IMediaStorage.PublicPrefix}motion/{job.TenantId:N}/{job.Id:N}/{name}";
            await using var content = await output.OpenAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            await storage.SaveAsync(key, buffer, output.ContentType, cancellationToken);
            outputs.Add(new MotionCaptureOutput(kind.Value, output.Angle, output.Format, key, storage.PublicUrl(key), buffer.Length));
        }

        if (outputs.Count == 0)
        {
            job.Fail("O serviço não devolveu nenhum arquivo 3D.", clock.GetUtcNow());
            return;
        }

        job.Succeed(outputs, clock.GetUtcNow());
    }

    private async Task DeleteSourceAsync(MotionCaptureJob job, CancellationToken cancellationToken)
    {
        if (!settings.DeleteSourceAfterProcessing || job.SourceMediaKey is not { } key)
        {
            return;
        }

        await storage.DeleteAsync(key, cancellationToken);
        job.SourceDeleted(clock.GetUtcNow());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Motion capture job {JobId} hit a transient error ({Reason})")]
    private static partial void LogTransient(ILogger logger, Guid jobId, string reason);
}
