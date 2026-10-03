using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Assistant;
using MyHealth.Application.ClientExperience;
using MyHealth.Application.Wellbeing;
using MyHealth.Domain.Assistant;

namespace MyHealth.ApiService.Endpoints;

/// <summary>
/// Less typing for the client: the daily wellbeing check-in, the small questions asked during a workout's rest, and the
/// assistant that turns what the client says into records they confirm.
/// </summary>
internal static class ClientExperienceEndpoints
{
    public sealed record WellbeingRequest(int? SleepQuality, int? Energy, Guid? WorkoutSessionId);

    public sealed record AssistantConsentRequest(int TermsVersion, IReadOnlyList<AssistantScope> Scopes);

    public sealed record AssistantMessageRequest(string Message, IReadOnlyList<AssistantTurn>? History, Guid? WorkoutSessionId, Guid? ExerciseId, int? SetNumber);

    public static void MapClientExperienceEndpoints(this IEndpointRouteBuilder routes)
    {
        var me = routes.MapGroup("/me");

        var wellbeing = me.MapGroup("/wellbeing").WithTags("Wellbeing");

        wellbeing.MapPut("/{date}", async (DateOnly date, WellbeingRequest request, ICommandHandler<SaveWellbeingCheckInCommand, WellbeingCheckInResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new SaveWellbeingCheckInCommand(date, request.SleepQuality, request.Energy, request.WorkoutSessionId), ct)).ToHttp());

        wellbeing.MapGet("/", async (DateOnly? from, DateOnly? to, IQueryHandler<ListMyWellbeingQuery, IReadOnlyList<WellbeingCheckInResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyWellbeingQuery(from, to), ct)).ToHttp());

        // The day's pending questions, most important first; pass the workout in progress to include its follow-ups.
        me.MapGet("/prompts", async (Guid? workoutSessionId, IQueryHandler<GetMyPromptsQuery, IReadOnlyList<PromptItem>> handler, CancellationToken ct) =>
                (await handler.Handle(new GetMyPromptsQuery(workoutSessionId), ct)).ToHttp())
            .WithTags("Prompts");

        var assistant = me.MapGroup("/assistant").WithTags("Assistant");

        assistant.MapGet("/", async (IQueryHandler<GetMyAssistantQuery, AssistantStatusResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMyAssistantQuery(), ct)).ToHttp());

        assistant.MapPut("/consent", async (AssistantConsentRequest request, ICommandHandler<AcceptAssistantTermsCommand, AssistantConsentResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new AcceptAssistantTermsCommand(request.TermsVersion, request.Scopes ?? []), ct)).ToHttp());

        assistant.MapDelete("/consent", async (ICommandHandler<WithdrawAssistantConsentCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new WithdrawAssistantConsentCommand(), ct)).ToHttp());

        assistant.MapPost("/messages", async (AssistantMessageRequest request, ICommandHandler<SendAssistantMessageCommand, AssistantReplyResponse> handler, CancellationToken ct) =>
                (await handler.Handle(
                    new SendAssistantMessageCommand(request.Message, request.History, request.WorkoutSessionId, request.ExerciseId, request.SetNumber), ct)).ToHttp())
            .RequireRateLimiting(RateLimitPolicies.Assistant);

        assistant.MapPost("/proposals/{proposalId:guid}/confirm", async (
                Guid proposalId, ICommandHandler<ConfirmAssistantProposalCommand, ConfirmProposalResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new ConfirmAssistantProposalCommand(proposalId), ct)).ToHttp());

        assistant.MapPost("/proposals/{proposalId:guid}/discard", async (Guid proposalId, ICommandHandler<DiscardAssistantProposalCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new DiscardAssistantProposalCommand(proposalId), ct)).ToHttp());

        // Professionals read check-ins only with a Wellbeing grant in that relationship.
        routes.MapGet("/care-relationships/{relationshipId:guid}/wellbeing", async (
                    Guid relationshipId, DateOnly? from, DateOnly? to, IQueryHandler<ListSharedWellbeingQuery, IReadOnlyList<WellbeingCheckInResponse>> handler,
                    CancellationToken ct) =>
                (await handler.Handle(new ListSharedWellbeingQuery(relationshipId, from, to), ct)).ToHttp())
            .WithTags("Care relationships");
    }
}
