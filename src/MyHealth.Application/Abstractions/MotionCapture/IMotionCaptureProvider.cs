using MyHealth.Domain.Training.Exercises;

namespace MyHealth.Application.Abstractions.MotionCapture;

/// <summary>The video to convert. <see cref="OpenAsync"/> may be called more than once (e.g. on a retry).</summary>
public sealed record MotionCaptureSource(string FileName, string ContentType, long SizeBytes, Func<CancellationToken, Task<Stream>> OpenAsync);

public enum MotionCaptureProviderState
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

public sealed record MotionCaptureProviderStatus(MotionCaptureProviderState State, int ProgressPercent, string? Error = null);

/// <summary>
/// One file of the result: a 3D model (<c>glb</c>, <c>fbx</c>) or a rendered video (<c>mp4</c>) from a camera angle.
/// MyHealth copies it to its own media storage, so the app never depends on the provider's links.
/// </summary>
public sealed record MotionCaptureProviderOutput(string Format, CameraAngle? Angle, string ContentType, Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>
/// Markerless motion capture: turns a filmed movement into an animated 3D character (DeepMotion Animate 3D, or another
/// provider through a new implementation). Calls may throw <see cref="HttpRequestException"/> or time out; the worker
/// retries with back-off. Selected with <c>MotionCapture:Provider</c>.
/// </summary>
public interface IMotionCaptureProvider
{
    string Name { get; }

    /// <summary>Uploads the video and starts the conversion. Returns the provider's job id.</summary>
    Task<string> SubmitAsync(MotionCaptureSource source, CancellationToken cancellationToken);

    Task<MotionCaptureProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken);

    /// <summary>The files of a job that succeeded.</summary>
    Task<IReadOnlyList<MotionCaptureProviderOutput>> GetOutputsAsync(string providerJobId, CancellationToken cancellationToken);
}
