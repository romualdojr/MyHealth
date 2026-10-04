using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Sentry;
using Sentry.Extensibility;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Errors, traces and logs to Sentry from the deployed environments only (Railway: Staging), when <c>Sentry:Dsn</c> (or
/// <c>SENTRY_DSN</c>) is set. Never in Development, even with a DSN configured, so local runs, debugging and tests send
/// nothing. Traces come from the existing OpenTelemetry instrumentation (ASP.NET Core, HTTP
/// clients, Npgsql), not from Sentry's own. Health data never leaves: no default PII (user, IP, cookies), no request
/// bodies, and e-mail addresses are masked in messages, exceptions, breadcrumbs and logs. Other settings bind from the
/// <c>Sentry</c> section (e.g. <c>Sentry:TracesSampleRate</c>, <c>Sentry:Release</c> or <c>SENTRY_RELEASE</c>).
/// </summary>
public static partial class SentryExtensions
{
    internal static string? SentryDsn(this IHostApplicationBuilder builder) => Dsn(builder) is { Length: > 0 } dsn ? dsn : null;

    public static TBuilder AddSentryMonitoring<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        if (Dsn(builder) is not { Length: > 0 } dsn || builder is not WebApplicationBuilder web)
        {
            return builder;
        }

        var service = builder.Environment.ApplicationName;
        var environment = builder.Environment.EnvironmentName.ToLowerInvariant();
        web.WebHost.UseSentry(options =>
        {
            options.Dsn = dsn;
            options.UseOtlp();
            options.TracesSampleRate ??= 1.0;
            options.EnableLogs = true;
            options.Environment ??= environment;
            options.DefaultTags["service"] = service;

            // Containers run as a non-root user without write access to the app folder: the installation id and the
            // offline queue live in the temp directory instead.
            options.CacheDirectoryPath ??= Path.Combine(Path.GetTempPath(), "sentry");

            options.SendDefaultPii = false;
            options.MaxRequestBodySize = RequestSize.None;
            options.SetBeforeSend(Scrub);
            options.SetBeforeBreadcrumb(crumb => crumb.Message is { } message && Email().IsMatch(message)
                ? new Breadcrumb(Mask(message), crumb.Type ?? "default", crumb.Data, crumb.Category, crumb.Level)
                : crumb);

            // Logs keep their template and parameters; a formatted message with an address is dropped rather than sent.
            options.SetBeforeSendLog(log => Email().IsMatch(log.Message) || IsFirstMigrationCheck(null, log.Message) ? null : log);
        });

        return builder;
    }

    private static string? Dsn(IHostApplicationBuilder builder) =>
        builder.Environment.IsDevelopment() ? null : builder.Configuration["Sentry:Dsn"] ?? builder.Configuration["SENTRY_DSN"];

    private static SentryEvent? Scrub(SentryEvent sentryEvent)
    {
        if (IsFirstMigrationCheck(sentryEvent.Logger, sentryEvent.Message?.Formatted ?? sentryEvent.Message?.Message))
        {
            return null;
        }

        if (sentryEvent.Message is { } message)
        {
            message.Formatted = message.Formatted is null ? null : Mask(message.Formatted);
            message.Message = message.Message is null ? null : Mask(message.Message);
        }

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = exception.Value is null ? null : Mask(exception.Value);
        }

        // Bearer tokens and the selected organization are not needed to read an error.
        sentryEvent.Request.Headers.Remove("Authorization");
        sentryEvent.Request.Headers.Remove("Cookie");
        sentryEvent.Request.Cookies = null;
        return sentryEvent;
    }

    /// <summary>
    /// On a new database EF Core reads the migrations history before the table exists and logs that query as an error,
    /// then creates it and migrates: expected once, not a problem.
    /// </summary>
    private static bool IsFirstMigrationCheck(string? logger, string? message) =>
        message?.Contains("__ef_migrations_history", StringComparison.Ordinal) == true
        && message.Contains("SELECT migration_id", StringComparison.Ordinal)
        && logger is null or "Microsoft.EntityFrameworkCore.Database.Command";

    private static string Mask(string text) => Email().Replace(text, "[email]");

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();
}
