using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Coaching;

namespace MyHealth.Infrastructure.Coaching;

/// <summary>
/// The trainer's coach on Claude (Anthropic Messages API with tool use). Selected with <c>Coach:Provider=Anthropic</c>;
/// the key comes from <c>Coach:Anthropic:ApiKey</c> (or the assistant's key) and the model from
/// <c>Coach:Anthropic:Model</c>. Only the pseudonymised context built for the request is sent; failures return
/// <c>null</c> and nothing about the request content is logged.
/// </summary>
internal sealed partial class AnthropicTrainingCoachModel(HttpClient http, IConfiguration configuration, ILogger<AnthropicTrainingCoachModel> logger)
    : ITrainingCoachModel
{
    public const string DefaultModel = "claude-sonnet-5";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "anthropic";

    public async Task<CoachModelReply?> AnalyzeAsync(CoachModelRequest request, CancellationToken cancellationToken)
    {
        var ask = "Analise como o cliente está respondendo a este plano e sugira ajustes, se houver.";
        if (request.Focus is { } focus)
        {
            ask += $"\nO treinador pediu atenção especial a: {focus}";
        }

        var body = new MessagesRequest(
            configuration["Coach:Anthropic:Model"] ?? DefaultModel,
            2048,
            $"{request.SystemPrompt}\n\nThe client's training records (JSON):\n{request.ContextJson}",
            [new Message("user", ask)],
            [.. request.Tools.Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema))]);

        try
        {
            using var response = await http.PostAsJsonAsync("v1/messages", body, Json, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogFailure(logger, (int)response.StatusCode);
                return null;
            }

            var reply = await response.Content.ReadFromJsonAsync<MessagesResponse>(Json, cancellationToken);
            if (reply?.Content is null)
            {
                return null;
            }

            var text = string.Join("\n", reply.Content.Where(c => c.Type == "text" && c.Text is not null).Select(c => c.Text));
            var calls = reply.Content
                .Where(c => c.Type == "tool_use" && c.Name is not null && c.Input is not null)
                .Select(c => new AssistantToolCall(c.Name!, c.Input!.Value))
                .ToList();
            return new CoachModelReply(text, calls);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogUnavailable(logger, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic coach request failed with HTTP {StatusCode}")]
    private static partial void LogFailure(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic coach unavailable ({Reason})")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    private sealed record MessagesRequest(
        string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        string System,
        IReadOnlyList<Message> Messages,
        IReadOnlyList<ToolDefinition> Tools);

    private sealed record Message(string Role, string Content);

    private sealed record ToolDefinition(string Name, string Description, [property: JsonPropertyName("input_schema")] JsonElement InputSchema);

    private sealed record MessagesResponse(IReadOnlyList<ContentBlock>? Content);

    private sealed record ContentBlock(string Type, string? Text, string? Name, JsonElement? Input);
}
