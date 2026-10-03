namespace MyHealth.Application.Abstractions.Media;

public sealed record StoredMedia(Stream Content, string ContentType, long Length);

/// <summary>
/// Object storage for media files (RF-27). Keys under <c>public/</c> are served by the media host (CDN) at
/// <see cref="PublicUrl"/>; everything else (e.g. a trainer's source video) is private and never served. The local
/// implementation is for development and tests; production uses a bucket behind the CDN with the same keys.
/// </summary>
public interface IMediaStorage
{
    public const string PublicPrefix = "public/";
    public const string PrivatePrefix = "private/";

    Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>Returns <c>null</c> when there is no such file.</summary>
    Task<StoredMedia?> OpenAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>The https address of a public file on the media host.</summary>
    string PublicUrl(string key);
}
