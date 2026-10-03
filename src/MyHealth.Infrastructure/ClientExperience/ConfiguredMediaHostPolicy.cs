using Microsoft.Extensions.Configuration;
using MyHealth.Application.Abstractions.Media;

namespace MyHealth.Infrastructure.ClientExperience;

/// <summary>
/// Accepts https URLs on the hosts in <c>Media:AllowedHosts</c> (default: MyHealth's media CDN). Subdomains of an
/// allowed host are accepted too.
/// </summary>
internal sealed class ConfiguredMediaHostPolicy(IConfiguration configuration) : IMediaHostPolicy
{
    public const string DefaultHost = "media.myhealth.app";

    private readonly string[] _hosts = configuration.GetSection("Media:AllowedHosts").Get<string[]>() is { Length: > 0 } hosts
        ? hosts.Select(h => h.Trim().ToLowerInvariant()).ToArray()
        : [DefaultHost];

    public bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && _hosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
}
