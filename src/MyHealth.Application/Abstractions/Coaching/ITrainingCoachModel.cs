using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Coaching;

namespace MyHealth.Application.Abstractions.Coaching;

/// <summary>
/// One analysis: the rules, the client's pseudonymised training records (<see cref="Context"/>, also serialized for
/// remote models), the trainer's optional focus and the tools the model may call to suggest changes.
/// </summary>
public sealed record CoachModelRequest(
    string SystemPrompt,
    CoachContext Context,
    string ContextJson,
    string? Focus,
    IReadOnlyList<AssistantToolDefinition> Tools);

/// <summary>The summary for the trainer and the changes the model suggests (tool calls, never executed directly).</summary>
public sealed record CoachModelReply(string Summary, IReadOnlyList<AssistantToolCall> ToolCalls);

/// <summary>
/// The language model behind the trainer's AI coach. The rule-based implementation (progressive overload and deload
/// heuristics) is the default and is used in tests; Claude is selected with <c>Coach:Provider=Anthropic</c>.
/// </summary>
public interface ITrainingCoachModel
{
    string Name { get; }

    /// <summary>Returns <c>null</c> when the model is unavailable.</summary>
    Task<CoachModelReply?> AnalyzeAsync(CoachModelRequest request, CancellationToken cancellationToken);
}
