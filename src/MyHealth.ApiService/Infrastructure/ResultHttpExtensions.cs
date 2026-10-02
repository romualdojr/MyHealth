using MyHealth.Domain.Abstractions;

namespace MyHealth.ApiService.Infrastructure;

/// <summary>Maps application results to HTTP: success → the given response, failure → RFC 9457 problem details.</summary>
internal static class ResultHttpExtensions
{
    public static IResult ToHttp(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();

    public static IResult ToHttp<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();

    public static IResult ToProblem(this Error error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        if (error is ValidationError validation)
        {
            return TypedResults.ValidationProblem(validation.Errors, error.Description, title: "Validation failed", extensions: extensions);
        }

        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        return TypedResults.Problem(error.Description, statusCode: status, title: error.Code, extensions: extensions);
    }
}

internal static class AuthorizationPolicies
{
    public const string Administrator = "Administrator";
}

internal static class RateLimitPolicies
{
    public const string Authentication = "authentication";

    /// <summary>Messages to the assistant, per user: each one calls the AI provider.</summary>
    public const string Assistant = "assistant";
}
