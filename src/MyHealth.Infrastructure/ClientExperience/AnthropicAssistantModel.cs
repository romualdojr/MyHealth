using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Assistant;

namespace MyHealth.Infrastructure.ClientExperience;

/// <summary>
/// The assistant on Claude (Anthropic Messages API with tool use). Selected with <c>Assistant:Provider=Anthropic</c>;
/// the key comes from <c>Assistant:Anthropic:ApiKey</c> (user secrets or a vault, never appsettings) and the model from
/// <c>Assistant:Anthropic:Model</c>. Only the context built for the request is sent. Failures return <c>null</c>, so the
/// app falls back to the forms; the request content is never logged.
/// </summary>
internal sealed partial class AnthropicAssistantModel(HttpClient http, IConfiguration configuration, ILogger<AnthropicAssistantModel> logger) : IAssistantModel
{
    public const string HttpClientName = "anthropic";
    public const string DefaultModel = "claude-sonnet-5";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "anthropic";

    public async Task<AssistantModelReply?> RespondAsync(AssistantModelRequest request, CancellationToken cancellationToken)
    {
        var body = new MessagesRequest(
            configuration["Assistant:Anthropic:Model"] ?? DefaultModel,
            1024,
            $"{request.SystemPrompt}\n\nThe client's data you may use (JSON):\n{request.ContextJson}",
            Conversation(request),
            request.Tools.Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema)).ToList());

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
                .Where(c => c.Type == "tool_use" && c.Name is not null && c.Input is { } input)
                .Select(c => new AssistantToolCall(c.Name!, c.Input!.Value))
                .ToList();
            return new AssistantModelReply(text, calls);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogUnavailable(logger, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The Messages API needs alternating turns starting with the user; earlier turns are merged accordingly.</summary>
    private static List<Message> Conversation(AssistantModelRequest request)
    {
        var turns = request.History.Append(new AssistantTurn(AssistantTurn.User, request.Message)).SkipWhile(t => t.Role != AssistantTurn.User);
        var messages = new List<Message>();
        foreach (var turn in turns)
        {
            if (messages.Count > 0 && messages[^1].Role == turn.Role)
            {
                messages[^1] = messages[^1] with { Content = $"{messages[^1].Content}\n{turn.Text}" };
            }
            else
            {
                messages.Add(new Message(turn.Role, turn.Text));
            }
        }

        return messages;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic assistant request failed with HTTP {StatusCode}")]
    private static partial void LogFailure(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic assistant unavailable ({Reason})")]
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
