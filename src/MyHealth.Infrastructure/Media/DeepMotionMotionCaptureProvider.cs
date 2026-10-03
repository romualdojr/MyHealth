using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.MotionCapture;
using MyHealth.Domain.Training.Exercises;

namespace MyHealth.Infrastructure.Media;

/// <summary>
/// DeepMotion Animate 3D (REST API): markerless motion capture from a single video. Selected with
/// <c>MotionCapture:Provider=DeepMotion</c>; needs <c>MotionCapture:DeepMotion:ApiBaseUrl</c> (the account's API host),
/// <c>ClientId</c> and <c>ClientSecret</c> (secrets: user secrets or a vault), and optionally <c>Model</c> (the
/// character id used for the render; DeepMotion's default otherwise).
/// <para>
/// Flow: <c>GET /session/auth</c> with Basic credentials → session cookie; <c>GET /upload?name=…</c> → signed URL;
/// <c>PUT</c> the video there; <c>POST /process</c> (processor <c>video2anim</c>, formats mp4, glb, fbx) → request id;
/// <c>GET /status/{rid}</c> until SUCCESS or FAILURE; <c>GET /download/{rid}</c> → file links (temporary; MyHealth
/// copies the files to its own storage). Validate request and response shapes against the account's API version and
/// sandbox before going live.
/// </para>
/// </summary>
/// <remarks>A singleton: it owns its <see cref="HttpClient"/>, whose cookie container keeps the DeepMotion session.</remarks>
internal sealed partial class DeepMotionMotionCaptureProvider(HttpClient http, IConfiguration configuration, ILogger<DeepMotionMotionCaptureProvider> logger)
    : IMotionCaptureProvider, IDisposable
{

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private DateTimeOffset _authenticatedUntil = DateTimeOffset.MinValue;

    public string Name => "deepmotion";

    public async Task<string> SubmitAsync(MotionCaptureSource source, CancellationToken cancellationToken)
    {
        await AuthenticateAsync(cancellationToken);

        var upload = await ReadAsync<UploadResponse>(
            await http.GetAsync($"upload?name={Uri.EscapeDataString(source.FileName)}&resumable=0", cancellationToken), cancellationToken);
        if (string.IsNullOrEmpty(upload?.Url))
        {
            throw new HttpRequestException("DeepMotion did not return an upload URL.");
        }

        // The signed URL points at the provider's storage, not at the API: sent without the session cookie.
        using (var storage = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            await using var content = await source.OpenAsync(cancellationToken);
            using var body = new StreamContent(content);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var put = await storage.PutAsync(upload.Url, body, cancellationToken);
            put.EnsureSuccessStatusCode();
        }

        List<string> parameters = ["config=configDefault", "formats=mp4,glb,fbx"];
        if (configuration["MotionCapture:DeepMotion:Model"] is { Length: > 0 } model)
        {
            parameters.Add($"model={model}");
        }

        var process = await ReadAsync<ProcessResponse>(
            await http.PostAsJsonAsync("process", new ProcessRequest(upload.Url, "video2anim", parameters), Json, cancellationToken), cancellationToken);
        return process?.Rid ?? throw new HttpRequestException("DeepMotion did not return a request id.");
    }

    public async Task<MotionCaptureProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken)
    {
        await AuthenticateAsync(cancellationToken);
        var response = await ReadAsync<StatusResponse>(await http.GetAsync($"status/{Uri.EscapeDataString(providerJobId)}", cancellationToken), cancellationToken);
        var statuses = response?.Status ?? [];
        var status = statuses.FirstOrDefault(s => s.Rid == providerJobId) ?? (statuses.Count > 0 ? statuses[0] : null);
        return status?.Status?.ToUpperInvariant() switch
        {
            "SUCCESS" => new MotionCaptureProviderStatus(MotionCaptureProviderState.Succeeded, 100),
            "FAILURE" => new MotionCaptureProviderStatus(MotionCaptureProviderState.Failed, 0, FailureText(status.Details)),
            _ => new MotionCaptureProviderStatus(
                MotionCaptureProviderState.Running,
                status?.Details is { Step: { } step, Total: > 0 and var total } ? (int)(100.0 * step / total) : 0),
        };
    }

    public async Task<IReadOnlyList<MotionCaptureProviderOutput>> GetOutputsAsync(string providerJobId, CancellationToken cancellationToken)
    {
        await AuthenticateAsync(cancellationToken);
        var response = await ReadAsync<DownloadResponse>(await http.GetAsync($"download/{Uri.EscapeDataString(providerJobId)}", cancellationToken), cancellationToken);

        var outputs = new List<MotionCaptureProviderOutput>();
        foreach (var file in (response?.Links ?? []).SelectMany(l => l.Urls ?? []).SelectMany(u => u.Files ?? []))
        {
            foreach (var (format, url) in file)
            {
                var (angle, type) = format switch
                {
                    // DeepMotion renders the character from the front camera.
                    "mp4" => ((CameraAngle?)CameraAngle.Front, "video/mp4"),
                    "glb" => (null, "model/gltf-binary"),
                    "fbx" => (null, "application/octet-stream"),
                    _ => (null, string.Empty),
                };
                if (type.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }

                outputs.Add(new MotionCaptureProviderOutput(format, angle, type, async ct =>
                {
                    // Download links are pre-signed storage URLs.
                    var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    var downloaded = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                    downloaded.EnsureSuccessStatusCode();
                    return new OwningStream(await downloaded.Content.ReadAsStreamAsync(ct), downloaded, client);
                }));
            }
        }

        return outputs;
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _authenticatedUntil)
        {
            return;
        }

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _authenticatedUntil)
            {
                return;
            }

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{configuration["MotionCapture:DeepMotion:ClientId"]}:{configuration["MotionCapture:DeepMotion:ClientSecret"]}"));
            using var request = new HttpRequestMessage(HttpMethod.Get, "session/auth");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogAuthenticationFailed(logger, (int)response.StatusCode);
                throw new HttpRequestException("DeepMotion authentication failed.", null, response.StatusCode);
            }

            // The session cookie (dmsess) is kept by the handler's cookie container; renewed well before it expires.
            _authenticatedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _authenticatedUntil = DateTimeOffset.MinValue;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogRequestFailed(logger, response.RequestMessage?.RequestUri?.AbsolutePath ?? "?", (int)response.StatusCode);
                throw new HttpRequestException($"DeepMotion returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
    }

    public void Dispose()
    {
        _authLock.Dispose();
        http.Dispose();
    }

    private static string FailureText(StatusDetails? details) =>
        "Não foi possível gerar o modelo 3D a partir deste vídeo. Grave de novo com o corpo inteiro visível, câmera parada e boa luz."
        + (details?.ExcMessage is { Length: > 0 } reason ? $" (DeepMotion: {reason[..Math.Min(reason.Length, 200)]})" : string.Empty);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DeepMotion authentication failed with HTTP {StatusCode}")]
    private static partial void LogAuthenticationFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DeepMotion request {Path} failed with HTTP {StatusCode}")]
    private static partial void LogRequestFailed(ILogger logger, string path, int statusCode);

    private sealed record UploadResponse(string? Url);

    private sealed record ProcessRequest(string Url, string Processor, IReadOnlyList<string> Params);

    private sealed record ProcessResponse(string? Rid);

    private sealed record StatusResponse(int Count, IReadOnlyList<JobStatus>? Status);

    private sealed record JobStatus(string? Rid, string? Status, StatusDetails? Details);

    private sealed record StatusDetails(int? Step, int? Total, [property: JsonPropertyName("exc_message")] string? ExcMessage);

    private sealed record DownloadResponse(int Count, IReadOnlyList<DownloadLink>? Links);

    private sealed record DownloadLink(string? Rid, IReadOnlyList<DownloadUrl>? Urls);

    private sealed record DownloadUrl(string? Name, IReadOnlyList<Dictionary<string, string>>? Files);

    /// <summary>A response stream that also disposes the response and the client that produced it.</summary>
    private sealed class OwningStream(Stream inner, HttpResponseMessage response, HttpClient client) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
