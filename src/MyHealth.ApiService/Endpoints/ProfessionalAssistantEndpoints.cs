using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.ProfessionalAssistant;
using MyHealth.Domain.Professionals;

namespace MyHealth.ApiService.Endpoints;

/// <summary>
/// The dietitian's and the therapy practitioner's chat: summaries of their patients and plan proposals (meal plans,
/// care plans) that they turn into plans through the usual endpoints. Trainers use <c>/trainer/assistant</c>.
/// </summary>
internal static class ProfessionalAssistantEndpoints
{
    public sealed record MessageRequest(string Message, IReadOnlyList<AssistantTurn>? History, Guid? ClientId);

    public static void MapProfessionalAssistantEndpoints(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGroup("/dietitian/assistant").WithTags("Nutrition"), Discipline.Dietitian);
        Map(routes.MapGroup("/therapy/assistant").WithTags("Therapies"), Discipline.TherapyPractitioner);
    }

    private static void Map(RouteGroupBuilder group, Discipline discipline)
    {
        group.MapGet("/", async (IQueryHandler<GetProfessionalAssistantOverviewQuery, ProfessionalAssistantOverview> handler, CancellationToken ct) =>
            (await handler.Handle(new GetProfessionalAssistantOverviewQuery(discipline), ct)).ToHttp());

        // Each message calls the AI provider: limited per user, like the other assistants.
        group.MapPost("/messages", async (
                MessageRequest request, ICommandHandler<SendProfessionalAssistantMessageCommand, ProfessionalAssistantReplyResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new SendProfessionalAssistantMessageCommand(discipline, request.Message, request.History, request.ClientId), ct)).ToHttp())
            .RequireRateLimiting(RateLimitPolicies.Assistant);
    }
}
