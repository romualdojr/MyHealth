using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.ProfessionalAssistant;

namespace MyHealth.Infrastructure.Coaching;

/// <summary>
/// The dietitian's and the therapy practitioner's chat on Claude. Selected with <c>ProfessionalAssistant:Provider=Anthropic</c>
/// (or <c>TrainerAssistant:Provider</c> when that is not set); the model comes from <c>ProfessionalAssistant:Anthropic:Model</c>.
/// Meal plans are long tool calls, hence the larger output budget. Only the pseudonymised context and conversation are
/// sent; failures return <c>null</c> and nothing about the content is logged.
/// </summary>
internal sealed partial class AnthropicProfessionalAssistantModel(
    HttpClient http, IConfiguration configuration, ILogger<AnthropicProfessionalAssistantModel> logger)
    : IProfessionalAssistantModel
{
    public string Name => "anthropic";

    public async Task<ProfessionalAssistantModelReply?> RespondAsync(ProfessionalAssistantModelRequest request, CancellationToken cancellationToken)
    {
        var reply = await AnthropicChat.SendAsync(
            http,
            configuration["ProfessionalAssistant:Anthropic:Model"] ?? configuration["TrainerAssistant:Anthropic:Model"] ?? AnthropicTrainerAssistantModel.DefaultModel,
            8192,
            $"{request.SystemPrompt}\n\nThe professional's patients (JSON):\n{request.ContextJson}",
            request.History,
            request.Message,
            request.Tools,
            status => LogFailure(logger, status),
            reason => LogUnavailable(logger, reason),
            cancellationToken);
        return reply is null ? null : new ProfessionalAssistantModelReply(reply.Text, reply.ToolCalls);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic professional assistant request failed with HTTP {StatusCode}")]
    private static partial void LogFailure(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic professional assistant unavailable ({Reason})")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
