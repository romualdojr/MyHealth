using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.TrainerAssistant;

namespace MyHealth.Infrastructure.Coaching;

/// <summary>
/// The trainer's chat on Claude (Anthropic Messages API with tool use). Selected with
/// <c>TrainerAssistant:Provider=Anthropic</c>; the key comes from <c>TrainerAssistant:Anthropic:ApiKey</c> (or the
/// coach's or the assistant's key) and the model from <c>TrainerAssistant:Anthropic:Model</c>. Only the pseudonymised
/// context and conversation are sent; failures return <c>null</c> and nothing about the content is logged.
/// </summary>
internal sealed partial class AnthropicTrainerAssistantModel(HttpClient http, IConfiguration configuration, ILogger<AnthropicTrainerAssistantModel> logger)
    : ITrainerAssistantModel
{
    public const string DefaultModel = "claude-sonnet-5";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Name => "anthropic";

    public async Task<TrainerAssistantModelReply?> RespondAsync(TrainerAssistantModelRequest request, CancellationToken cancellationToken)
    {
        var body = new MessagesRequest(
            configuration["TrainerAssistant:Anthropic:Model"] ?? DefaultModel,
            2048,
            $"{request.SystemPrompt}\n\nThe trainer's students (JSON):\n{request.ContextJson}",
            Conversation(request),
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
            return new TrainerAssistantModelReply(text, calls);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogUnavailable(logger, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The Messages API needs alternating turns starting with the user; consecutive turns are merged.</summary>
    private static List<Message> Conversation(TrainerAssistantModelRequest request)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic trainer assistant request failed with HTTP {StatusCode}")]
    private static partial void LogFailure(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic trainer assistant unavailable ({Reason})")]
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
