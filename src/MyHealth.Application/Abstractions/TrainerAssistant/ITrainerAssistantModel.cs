using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.TrainerAssistant;

namespace MyHealth.Application.Abstractions.TrainerAssistant;

/// <summary>
/// One chat message of the trainer: the rules, the pseudonymised records of the trainer's students who allow AI
/// analysis (<see cref="Context"/>, also serialized for remote models), the recent conversation and the new message.
/// </summary>
public sealed record TrainerAssistantModelRequest(
    string SystemPrompt,
    TrainerAssistantContext Context,
    string ContextJson,
    IReadOnlyList<AssistantTurn> History,
    string Message,
    IReadOnlyList<AssistantToolDefinition> Tools);

/// <summary>The answer and the plan changes the model suggests (tool calls, never executed directly).</summary>
public sealed record TrainerAssistantModelReply(string Text, IReadOnlyList<AssistantToolCall> ToolCalls);

/// <summary>
/// The language model behind the trainer's chat. The rule-based implementation (summaries and the coach's progression
/// rules) is the default and is used in tests; Claude is selected with <c>TrainerAssistant:Provider=Anthropic</c>.
/// </summary>
public interface ITrainerAssistantModel
{
    string Name { get; }

    /// <summary>Returns <c>null</c> when the model is unavailable.</summary>
    Task<TrainerAssistantModelReply?> RespondAsync(TrainerAssistantModelRequest request, CancellationToken cancellationToken);
}
