using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.ProfessionalAssistant;

namespace MyHealth.Application.Abstractions.ProfessionalAssistant;

/// <summary>
/// One chat message of a dietitian or a therapy practitioner: the rules for their discipline, the pseudonymised records
/// of their patients who allow AI analysis (<see cref="Context"/>, also serialized for remote models), the recent
/// conversation and the new message.
/// </summary>
public sealed record ProfessionalAssistantModelRequest(
    string SystemPrompt,
    ProfessionalAssistantContext Context,
    string ContextJson,
    IReadOnlyList<AssistantTurn> History,
    string Message,
    IReadOnlyList<AssistantToolDefinition> Tools);

/// <summary>The answer and the plans the model proposes (tool calls, never executed directly).</summary>
public sealed record ProfessionalAssistantModelReply(string Text, IReadOnlyList<AssistantToolCall> ToolCalls);

/// <summary>
/// The language model behind the dietitian's and the therapy practitioner's chat. The rule-based implementation is the
/// default and is used in tests; Claude is selected with <c>ProfessionalAssistant:Provider=Anthropic</c> (or, when that is
/// not set, <c>TrainerAssistant:Provider=Anthropic</c>).
/// </summary>
public interface IProfessionalAssistantModel
{
    string Name { get; }

    /// <summary>Returns <c>null</c> when the model is unavailable.</summary>
    Task<ProfessionalAssistantModelReply?> RespondAsync(ProfessionalAssistantModelRequest request, CancellationToken cancellationToken);
}
