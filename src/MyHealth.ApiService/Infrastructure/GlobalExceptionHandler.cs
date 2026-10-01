using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MyHealth.ApiService.Infrastructure;

/// <summary>
/// Last-resort handler. Concurrent writes (two retries racing, optimistic concurrency) become 409 so clients retry;
/// anything else is a 500 without internal details.
/// </summary>
internal sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Concurrency.Conflict"),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "Concurrency.Duplicate"),
            BadHttpRequestException bad => (bad.StatusCode, "Request.Invalid"),
            _ => (StatusCodes.Status500InternalServerError, "Server.Error"),
        };

        if (status >= 500)
        {
            // Exception messages may embed request data; log the exception type and stack only through the logger pipeline.
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Detail = status == StatusCodes.Status409Conflict ? "The resource was changed concurrently. Retry the request." : null,
            },
        });
    }
}
