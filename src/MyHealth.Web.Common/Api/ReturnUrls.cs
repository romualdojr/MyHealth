namespace MyHealth.Web.Common.Api;

public static class ReturnUrls
{
    /// <summary>
    /// The local path to go to after sign-in. Anything that could leave the site (absolute URLs, protocol-relative
    /// <c>//host</c> or <c>/\host</c> forms, schemes) becomes the home page, so a crafted link cannot redirect elsewhere.
    /// </summary>
    public static string Sanitize(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var trimmed = returnUrl.Trim();
        var unsafePath = trimmed.Contains(':') || trimmed.Contains('\\') || trimmed.StartsWith("//", StringComparison.Ordinal);
        var path = "/" + trimmed.TrimStart('/');
        return unsafePath || !Uri.IsWellFormedUriString(path, UriKind.Relative) ? "/" : path;
    }
}
