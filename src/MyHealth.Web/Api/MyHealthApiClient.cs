using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyHealth.Web.Api;

/// <summary>An RFC 9457 problem returned by the API, reduced to what the UI shows.</summary>
public sealed record ApiProblem(int Status, string Code, string? Detail, IReadOnlyDictionary<string, string[]>? Errors)
{
    public string Message =>
        Errors is { Count: > 0 }
            ? string.Join(" ", Errors.SelectMany(e => e.Value))
            : Detail ?? Code;

    public static readonly ApiProblem SessionExpired = new(401, "Session.Expired", "Sua sessão expirou. Entre novamente.", null);
}

public sealed class ApiResult<T>
{
    private ApiResult(T? value, ApiProblem? problem)
    {
        Value = value;
        Problem = problem;
    }

    public T? Value { get; }

    public ApiProblem? Problem { get; }

    public bool IsSuccess => Problem is null;

    public static ApiResult<T> Success(T? value) => new(value, null);

    public static ApiResult<T> Failure(ApiProblem problem) => new(default, problem);
}

/// <summary>
/// Calls the MyHealth API as the signed-in user, sending the bearer token and the selected organization
/// (<c>X-Tenant-Id</c>) on every request. A 401 triggers one token refresh and a retry.
/// </summary>
public sealed class MyHealthApiClient(IHttpClientFactory httpClients, ApiSession session)
{
    public const string HttpClientName = "myhealth-api";
    public const string TenantHeader = "X-Tenant-Id";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    public Task<ApiResult<T>> PostAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Post, path, body, ct);

    public Task<ApiResult<T>> PutAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Put, path, body, ct);

    public Task<ApiResult<NoContent>> PostAsync(string path, object? body = null, CancellationToken ct = default) =>
        SendAsync<NoContent>(HttpMethod.Post, path, body, ct);

    public Task<ApiResult<NoContent>> PutAsync(string path, object? body = null, CancellationToken ct = default) =>
        SendAsync<NoContent>(HttpMethod.Put, path, body, ct);

    public Task<ApiResult<NoContent>> DeleteAsync(string path, CancellationToken ct = default) => SendAsync<NoContent>(HttpMethod.Delete, path, null, ct);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = await session.GetAccessTokenAsync(ct);
        if (token is null)
        {
            return ApiResult<T>.Failure(ApiProblem.SessionExpired);
        }

        using var response = await SendOnceAsync(method, path, body, token, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && await session.RefreshAsync(ct) is { } refreshed)
        {
            using var retried = await SendOnceAsync(method, path, body, refreshed, ct);
            return await ReadAsync<T>(retried, ct);
        }

        return await ReadAsync<T>(response, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "/api/v1/" + path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session.TenantId is { } tenantId)
        {
            request.Headers.Add(TenantHeader, tenantId.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
    }

    internal static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            if (typeof(T) == typeof(NoContent) || response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            {
                return ApiResult<T>.Success(default);
            }

            return ApiResult<T>.Success(await response.Content.ReadFromJsonAsync<T>(Json, ct));
        }

        return ApiResult<T>.Failure(await ReadProblemAsync(response, ct));
    }

    internal static async Task<ApiProblem> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return ApiProblem.SessionExpired;
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Json, ct);
            if (problem is not null)
            {
                return new ApiProblem(status, problem.Code ?? problem.Title ?? $"HTTP {status}", problem.Detail, problem.Errors);
            }
        }
        catch (JsonException)
        {
            // Not a problem document (e.g. a proxy error page); fall through to a generic message.
        }

        return new ApiProblem(status, $"HTTP {status}", status == 403 ? "Você não tem permissão para esta ação." : null, null);
    }

    private sealed record ProblemBody(string? Title, string? Detail, string? Code, Dictionary<string, string[]>? Errors);
}

/// <summary>Marker for calls that return no body.</summary>
public sealed record NoContent;
