using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyHealth.Application.Abstractions.Assistant;

namespace MyHealth.Infrastructure.Coaching;

/// <summary>
/// One call to the Anthropic Messages API with tool use, shared by the professionals' chats. Returns <c>null</c> on any
/// failure; the caller logs it without content.
/// </summary>
internal static class AnthropicChat
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed record Reply(string Text, IReadOnlyList<AssistantToolCall> ToolCalls);

    /// <param name="onFailure">Called with the HTTP status of a rejected request.</param>
    /// <param name="onUnavailable">Called with the exception type when the provider could not be reached or answered garbage.</param>
    public static async Task<Reply?> SendAsync(
        HttpClient http, string model, int maxTokens, string system, IReadOnlyList<AssistantTurn> history, string message, IReadOnlyList<AssistantToolDefinition> tools,
        Action<int> onFailure, Action<string> onUnavailable, CancellationToken cancellationToken)
    {
        var body = new MessagesRequest(
            model, maxTokens, system, Conversation(history, message), [.. tools.Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema))]);

        try
        {
            using var response = await http.PostAsJsonAsync("v1/messages", body, Json, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                onFailure((int)response.StatusCode);
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
            return new Reply(text, calls);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            onUnavailable(ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The Messages API needs alternating turns starting with the user; consecutive turns are merged.</summary>
    private static List<Message> Conversation(IReadOnlyList<AssistantTurn> history, string message)
    {
        var turns = history.Append(new AssistantTurn(AssistantTurn.User, message)).SkipWhile(t => t.Role != AssistantTurn.User);
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
