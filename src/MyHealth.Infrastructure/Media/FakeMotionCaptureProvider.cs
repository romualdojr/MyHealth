using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.MotionCapture;
using MyHealth.Domain.Training.Exercises;

namespace MyHealth.Infrastructure.Media;

/// <summary>
/// Stands in for DeepMotion in development and tests. A job "finishes" on the first status check and returns the
/// uploaded video as the side, front and back renders plus a small placeholder 3D model (a valid glTF binary), so the
/// whole flow — storage, review, accepting into the exercise, the app's 3D viewer — works without the provider.
/// </summary>
internal sealed partial class FakeMotionCaptureProvider(ILogger<FakeMotionCaptureProvider> logger) : IMotionCaptureProvider
{
    private readonly ConcurrentDictionary<string, byte[]> _sources = new();

    public string Name => "fake";

    public async Task<string> SubmitAsync(MotionCaptureSource source, CancellationToken cancellationToken)
    {
        await using var content = await source.OpenAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var id = $"fake-{Guid.CreateVersion7()}";
        _sources[id] = buffer.ToArray();
        LogSubmitted(logger, id, source.SizeBytes);
        return id;
    }

    public Task<MotionCaptureProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) =>
        Task.FromResult(_sources.ContainsKey(providerJobId)
            ? new MotionCaptureProviderStatus(MotionCaptureProviderState.Succeeded, 100)
            : new MotionCaptureProviderStatus(MotionCaptureProviderState.Failed, 0, "Job desconhecido pelo provedor de teste."));

    public Task<IReadOnlyList<MotionCaptureProviderOutput>> GetOutputsAsync(string providerJobId, CancellationToken cancellationToken)
    {
        var video = _sources.GetValueOrDefault(providerJobId) ?? [];
        Func<CancellationToken, Task<Stream>> Bytes(byte[] bytes) => _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

        IReadOnlyList<MotionCaptureProviderOutput> outputs =
        [
            new("mp4", CameraAngle.Side, "video/mp4", Bytes(video)),
            new("mp4", CameraAngle.Front, "video/mp4", Bytes(video)),
            new("mp4", CameraAngle.Back, "video/mp4", Bytes(video)),
            new("glb", null, "model/gltf-binary", Bytes(PlaceholderModel())),
        ];
        return Task.FromResult(outputs);
    }

    /// <summary>A glTF 2.0 binary with a single triangle: the smallest model a 3D viewer will open.</summary>
    internal static byte[] PlaceholderModel()
    {
        var json = Encoding.UTF8.GetBytes("""
            {"asset":{"version":"2.0","generator":"MyHealth fake motion capture"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
            "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"buffers":[{"byteLength":36}],
            "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36,"target":34962}],
            "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}]}
            """.ReplaceLineEndings(string.Empty));
        var jsonPadded = Pad(json, (byte)' ');
        var positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var bin = new byte[36];
        Buffer.BlockCopy(positions, 0, bin, 0, bin.Length);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u); // "glTF"
        writer.Write(2u);
        writer.Write((uint)(12 + 8 + jsonPadded.Length + 8 + bin.Length));
        writer.Write((uint)jsonPadded.Length);
        writer.Write(0x4E4F534Au); // "JSON"
        writer.Write(jsonPadded);
        writer.Write((uint)bin.Length);
        writer.Write(0x004E4942u); // "BIN"
        writer.Write(bin);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Pad(byte[] bytes, byte filler)
    {
        var padded = new byte[(bytes.Length + 3) / 4 * 4];
        bytes.CopyTo(padded, 0);
        Array.Fill(padded, filler, bytes.Length, padded.Length - bytes.Length);
        return padded;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fake motion capture job {JobId} received {Bytes} bytes")]
    private static partial void LogSubmitted(ILogger logger, string jobId, long bytes);
}
