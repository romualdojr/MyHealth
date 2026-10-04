using QRCoder;

namespace MyHealth.Web.Common.Api;

/// <summary>
/// The authenticator setup as a QR code: the standard <c>otpauth://totp</c> URI (Google Authenticator key URI format,
/// understood by every authenticator app) for the shared key the API generated, drawn as inline SVG.
/// </summary>
public static class AuthenticatorQrCode
{
    public const string Issuer = "MyHealth";

    public static string Uri(string email, string sharedKey)
    {
        var label = System.Uri.EscapeDataString($"{Issuer}:{email}");
        var secret = sharedKey.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return $"otpauth://totp/{label}?secret={secret}&issuer={System.Uri.EscapeDataString(Issuer)}&digits=6&period=30";
    }

    /// <summary>An SVG that scales with its container (viewBox), dark modules on a white quiet zone.</summary>
    public static string Svg(string email, string sharedKey)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(Uri(email, sharedKey), QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#0f172a", "#ffffff", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
