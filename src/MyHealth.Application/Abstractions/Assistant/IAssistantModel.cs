using System.Text.Json;
using MyHealth.Application.Assistant;

namespace MyHealth.Application.Abstractions.Assistant;

public sealed record AssistantTurn(string Role, string Text)
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>A write the model wants to make. It never runs: it becomes a suggestion the client confirms.</summary>
public sealed record AssistantToolCall(string Name, JsonElement Input);

public sealed record AssistantToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// One request: the rules, the client's data the consent allows (<see cref="Context"/>, also serialized for remote
/// models), the recent conversation and the new message.
/// </summary>
public sealed record AssistantModelRequest(
    string SystemPrompt,
    AssistantContext Context,
    string ContextJson,
    IReadOnlyList<AssistantTurn> History,
    string Message,
    IReadOnlyList<AssistantToolDefinition> Tools);

public sealed record AssistantModelReply(string Text, IReadOnlyList<AssistantToolCall> ToolCalls);

/// <summary>
/// The language model behind the assistant. The fake implementation understands simple Portuguese phrases and is used in
/// development and tests; the Claude implementation is selected with <c>Assistant:Provider=Anthropic</c>.
/// </summary>
public interface IAssistantModel
{
    string Name { get; }

    /// <summary>Returns <c>null</c> when the model is unavailable; the client then falls back to the forms.</summary>
    Task<AssistantModelReply?> RespondAsync(AssistantModelRequest request, CancellationToken cancellationToken);
}
