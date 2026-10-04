using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

    public string Name => "anthropic";

    public async Task<TrainerAssistantModelReply?> RespondAsync(TrainerAssistantModelRequest request, CancellationToken cancellationToken)
    {
        var reply = await AnthropicChat.SendAsync(
            http,
            configuration["TrainerAssistant:Anthropic:Model"] ?? DefaultModel,
            2048,
            $"{request.SystemPrompt}\n\nThe trainer's students (JSON):\n{request.ContextJson}",
            request.History,
            request.Message,
            request.Tools,
            status => LogFailure(logger, status),
            reason => LogUnavailable(logger, reason),
            cancellationToken);
        return reply is null ? null : new TrainerAssistantModelReply(reply.Text, reply.ToolCalls);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic trainer assistant request failed with HTTP {StatusCode}")]
    private static partial void LogFailure(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anthropic trainer assistant unavailable ({Reason})")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
