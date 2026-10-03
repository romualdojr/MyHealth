namespace MyHealth.Application.Abstractions.Media;

/// <summary>
/// Exercise media must come from MyHealth's own media host (CDN), so the app never plays videos from arbitrary sites and
/// what is published is owned or licensed. Configured with <c>Media:AllowedHosts</c>.
/// </summary>
public interface IMediaHostPolicy
{
    bool IsAllowed(string url);
}
