using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.WhatsApp;
using MyHealth.Application.Messaging;

namespace MyHealth.ApiService.Endpoints;

/// <summary>WhatsApp opt-in for any user, click-to-chat for professionals, and the provider's webhook (RF-08).</summary>
internal static class WhatsAppEndpoints
{
    public sealed record OptInRequest(string PhoneNumber, bool AllowProfessionalContact);

    public sealed record ConfirmRequest(string Code);

    public sealed record ProfessionalContactRequest(bool Allow);

    public static void MapWhatsAppEndpoints(this IEndpointRouteBuilder routes, IEndpointRouteBuilder api)
    {
        var me = routes.MapGroup("/me/whatsapp").WithTags("WhatsApp");

        me.MapGet("/", async (IQueryHandler<GetMyWhatsAppQuery, WhatsAppStatusResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMyWhatsAppQuery(), ct)).ToHttp());

        // Sends a code to the number; each request reaches the provider, so it is rate-limited per user.
        me.MapPut("/", async (OptInRequest request, ICommandHandler<StartWhatsAppOptInCommand, WhatsAppStatusResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new StartWhatsAppOptInCommand(request.PhoneNumber, request.AllowProfessionalContact), ct)).ToHttp())
            .RequireRateLimiting(RateLimitPolicies.Assistant);

        me.MapPost("/confirm", async (ConfirmRequest request, ICommandHandler<ConfirmWhatsAppCommand, WhatsAppStatusResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new ConfirmWhatsAppCommand(request.Code), ct)).ToHttp());

        me.MapPut("/professional-contact", async (
                ProfessionalContactRequest request, ICommandHandler<SetWhatsAppProfessionalContactCommand, WhatsAppStatusResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new SetWhatsAppProfessionalContactCommand(request.Allow), ct)).ToHttp());

        me.MapDelete("/", async (ICommandHandler<OptOutOfWhatsAppCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new OptOutOfWhatsAppCommand(), ct)).ToHttp());

        routes.MapGet("/care-relationships/{relationshipId:guid}/whatsapp", async (
                Guid relationshipId, IQueryHandler<GetClientWhatsAppQuery, ProfessionalWhatsAppResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetClientWhatsAppQuery(relationshipId), ct)).ToHttp())
            .WithTags("WhatsApp");

        // The provider calls these; they authenticate with the body signature or the verify token, not a user token.
        api.MapGet("/whatsapp/webhooks", (HttpRequest request, IWhatsAppProvider provider) =>
            {
                var query = request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
                return provider.AnswerVerification(query) is { } challenge ? Results.Text(challenge) : Results.StatusCode(StatusCodes.Status403Forbidden);
            })
            .AllowAnonymous()
            .WithTags("WhatsApp (provider)");

        api.MapPost("/whatsapp/webhooks", async (HttpRequest request, ICommandHandler<ProcessWhatsAppWebhookCommand, WhatsAppWebhookResult> handler, CancellationToken ct) =>
            {
                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync(ct);
                var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                return (await handler.Handle(new ProcessWhatsAppWebhookCommand(body, headers), ct)).ToHttp();
            })
            .AllowAnonymous()
            .WithTags("WhatsApp (provider)");
    }
}
