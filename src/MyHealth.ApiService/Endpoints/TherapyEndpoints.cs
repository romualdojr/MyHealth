using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Therapies;
using MyHealth.Domain.Therapies;

namespace MyHealth.ApiService.Endpoints;

/// <summary>
/// Integrative therapies (RF-33 → RF-36). Practitioners act under <c>/therapy</c> for the selected tenant; clients use
/// <c>/me/therapy-*</c>; platform operators configure modalities, consent texts and qualifications under
/// <c>/admin</c>. Nothing here works for a modality that has not been enabled after legal review.
/// </summary>
internal static class TherapyEndpoints
{
    public sealed record ServiceUpdateRequest(string Name, string? Description, int DurationMinutes, ServiceDelivery Delivery, bool IsActive);

    public sealed record RescheduleRequest(DateTimeOffset StartsAt);

    public sealed record TherapyReasonRequest(string? Reason);

    public sealed record CorrectRecordRequest(TherapyRecordInput Record, string Reason);

    public sealed record ReferralRequest(string Reason, ReferralUrgency Urgency);

    public sealed record ProgressRequest(int Wellbeing, string? Comments);

    public sealed record EnableModalityRequest(string LegalReviewReference);

    public sealed record ConsentTemplateRequest(string Title, string Body, IReadOnlyList<string> DisclosedRisks);

    public sealed record DecisionNotes(string? Notes);

    public static void MapTherapyEndpoints(this IEndpointRouteBuilder routes)
    {
        MapPractitionerEndpoints(routes.MapGroup("/therapy").WithTags("Therapies"));
        MapClientEndpoints(routes.MapGroup("/me").WithTags("Therapies"));
        MapAdminEndpoints(routes.MapGroup("/admin").WithTags("Administration").RequireAuthorization(AuthorizationPolicies.Administrator));

        routes.MapGet("/care-relationships/{relationshipId:guid}/therapy-summary", async (
                    Guid relationshipId, IQueryHandler<GetSharedTherapySummaryQuery, TherapySummaryResponse> handler, CancellationToken ct) =>
                (await handler.Handle(new GetSharedTherapySummaryQuery(relationshipId), ct)).ToHttp())
            .WithTags("Care relationships");
    }

    private static void MapPractitionerEndpoints(RouteGroupBuilder therapy)
    {
        therapy.MapGet("/modalities", async (IQueryHandler<ListModalitiesQuery, IReadOnlyList<ModalityResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListModalitiesQuery(IncludeDisabled: false), ct)).ToHttp());

        therapy.MapGet("/modalities/{code}/consent", async (
                string code, IQueryHandler<GetCurrentConsentTemplateQuery, ConsentTemplateResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetCurrentConsentTemplateQuery(code), ct)).ToHttp());

        therapy.MapPost("/qualifications", async (
                SubmitQualificationCommand command, ICommandHandler<SubmitQualificationCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created("/api/v1/therapy/qualifications", new { id })));

        therapy.MapGet("/qualifications", async (IQueryHandler<ListMyQualificationsQuery, IReadOnlyList<QualificationResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyQualificationsQuery(), ct)).ToHttp());

        therapy.MapGet("/services", async (IQueryHandler<ListTenantTherapyServicesQuery, IReadOnlyList<TherapyServiceResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListTenantTherapyServicesQuery(), ct)).ToHttp());

        therapy.MapPost("/services", async (
                OfferTherapyServiceCommand command, ICommandHandler<OfferTherapyServiceCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created("/api/v1/therapy/services", new { id })));

        therapy.MapPut("/services/{serviceId:guid}", async (
                Guid serviceId, ServiceUpdateRequest r, ICommandHandler<UpdateTherapyServiceCommand> handler, CancellationToken ct) =>
            (await handler.Handle(
                new UpdateTherapyServiceCommand(serviceId, new TherapyServiceInput(r.Name, r.Description, r.DurationMinutes, r.Delivery), r.IsActive), ct)).ToHttp());

        var sessions = therapy.MapGroup("/sessions");

        sessions.MapPost("/", async (
                ScheduleTherapySessionCommand command, ICommandHandler<ScheduleTherapySessionCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created($"/api/v1/therapy/sessions/{id}", new { id })));

        sessions.MapGet("/", async (
                Guid? clientId, DateTimeOffset? from, DateTimeOffset? to,
                IQueryHandler<ListPractitionerSessionsQuery, IReadOnlyList<TherapySessionResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListPractitionerSessionsQuery(clientId, from, to), ct)).ToHttp());

        // Readable by the client and by the practitioner (in the session's tenant).
        sessions.MapGet("/{sessionId:guid}", async (
                Guid sessionId, IQueryHandler<GetTherapySessionQuery, TherapySessionResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetTherapySessionQuery(sessionId), ct)).ToHttp());

        sessions.MapPost("/{sessionId:guid}/reschedule", async (
                Guid sessionId, RescheduleRequest request, ICommandHandler<RescheduleTherapySessionCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RescheduleTherapySessionCommand(sessionId, request.StartsAt), ct)).ToHttp());

        sessions.MapPost("/{sessionId:guid}/cancel", async (
                Guid sessionId, TherapyReasonRequest? request, ICommandHandler<CancelTherapySessionCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new CancelTherapySessionCommand(sessionId, request?.Reason), ct)).ToHttp());

        sessions.MapPut("/{sessionId:guid}/record", async (
                Guid sessionId, TherapyRecordInput record, ICommandHandler<DocumentTherapySessionCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new DocumentTherapySessionCommand(sessionId, record), ct)).ToHttp());

        sessions.MapPost("/{sessionId:guid}/complete", async (Guid sessionId, ICommandHandler<CompleteTherapySessionCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new CompleteTherapySessionCommand(sessionId), ct)).ToHttp());

        sessions.MapPost("/{sessionId:guid}/record/corrections", async (
                Guid sessionId, CorrectRecordRequest request, ICommandHandler<CorrectTherapyRecordCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new CorrectTherapyRecordCommand(sessionId, request.Record, request.Reason), ct)).ToHttp());

        sessions.MapPost("/{sessionId:guid}/medical-referral", async (
                Guid sessionId, ReferralRequest request, ICommandHandler<RecommendMedicalCareCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RecommendMedicalCareCommand(sessionId, request.Reason, request.Urgency), ct)).ToHttp());

        therapy.MapGet("/clients/{clientId:guid}/consents", async (
                Guid clientId, IQueryHandler<ListClientConsentsQuery, IReadOnlyList<ConsentResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListClientConsentsQuery(clientId), ct)).ToHttp());

        therapy.MapGet("/clients/{clientId:guid}/care-plans", async (
                Guid clientId, IQueryHandler<ListClientCarePlansQuery, IReadOnlyList<CarePlanResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListClientCarePlansQuery(clientId), ct)).ToHttp());

        therapy.MapPost("/care-plans", async (StartCarePlanCommand command, ICommandHandler<StartCarePlanCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created("/api/v1/therapy/care-plans", new { id })));

        therapy.MapPost("/care-plans/{carePlanId:guid}/reviews", async (
                Guid carePlanId, CarePlanReviewRequest r, ICommandHandler<ReviewCarePlanCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ReviewCarePlanCommand(carePlanId, r.Outcome, r.Notes, r.Goals, r.PlannedSessions, r.Frequency, r.NextReviewOn), ct)).ToHttp());
    }

    public sealed record CarePlanReviewRequest(
        CarePlanReviewOutcome Outcome, string Notes, string? Goals, int? PlannedSessions, string? Frequency, DateOnly? NextReviewOn);

    public sealed record ConsentDecisionRequest(Guid RelationshipId, string ModalityCode, int TemplateVersion, ConsentDecision Decision);

    private static void MapClientEndpoints(RouteGroupBuilder me)
    {
        me.MapGet("/therapy-services", async (IQueryHandler<ListMyTherapyServicesQuery, IReadOnlyList<TherapyServiceResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyTherapyServicesQuery(), ct)).ToHttp());

        me.MapGet("/therapy-sessions", async (IQueryHandler<ListMyTherapySessionsQuery, IReadOnlyList<TherapySessionResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyTherapySessionsQuery(), ct)).ToHttp());

        me.MapPost("/therapy-sessions/{sessionId:guid}/cancel", async (
                Guid sessionId, TherapyReasonRequest? request, ICommandHandler<CancelMyTherapySessionCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new CancelMyTherapySessionCommand(sessionId, request?.Reason), ct)).ToHttp());

        me.MapPost("/therapy-sessions/{sessionId:guid}/progress", async (
                Guid sessionId, ProgressRequest request, ICommandHandler<ReportTherapyProgressCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ReportTherapyProgressCommand(sessionId, request.Wellbeing, request.Comments), ct)).ToHttp());

        me.MapGet("/therapy-consents", async (IQueryHandler<ListMyConsentsQuery, IReadOnlyList<ConsentResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyConsentsQuery(), ct)).ToHttp());

        me.MapPost("/therapy-consents", async (ConsentDecisionRequest r, ICommandHandler<DecideConsentCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(new DecideConsentCommand(r.RelationshipId, r.ModalityCode, r.TemplateVersion, r.Decision), ct))
            .ToHttp(id => TypedResults.Created("/api/v1/me/therapy-consents", new { id })));

        me.MapPost("/therapy-consents/{consentId:guid}/withdraw", async (
                Guid consentId, TherapyReasonRequest? request, ICommandHandler<WithdrawConsentCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new WithdrawConsentCommand(consentId, request?.Reason), ct)).ToHttp());

        me.MapGet("/therapy-care-plans", async (IQueryHandler<ListMyCarePlansQuery, IReadOnlyList<CarePlanResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyCarePlansQuery(), ct)).ToHttp());
    }

    private static void MapAdminEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/therapy-modalities", async (IQueryHandler<ListModalitiesQuery, IReadOnlyList<ModalityResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListModalitiesQuery(IncludeDisabled: true), ct)).ToHttp());

        admin.MapPost("/therapy-modalities", async (DefineModalityCommand command, ICommandHandler<DefineModalityCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created("/api/v1/admin/therapy-modalities", new { id })));

        admin.MapPut("/therapy-modalities/{code}", async (
                string code, ModalityInput modality, ICommandHandler<ConfigureModalityCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ConfigureModalityCommand(code, modality), ct)).ToHttp());

        admin.MapPost("/therapy-modalities/{code}/enable", async (
                string code, EnableModalityRequest request, ICommandHandler<ChangeModalityStatusCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeModalityStatusCommand(code, Enable: true, request.LegalReviewReference), ct)).ToHttp());

        admin.MapPost("/therapy-modalities/{code}/disable", async (string code, ICommandHandler<ChangeModalityStatusCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeModalityStatusCommand(code, Enable: false, null), ct)).ToHttp());

        admin.MapPost("/therapy-modalities/{code}/consent-templates", async (
                string code, ConsentTemplateRequest r, ICommandHandler<PublishConsentTemplateCommand, int> handler, CancellationToken ct) =>
            (await handler.Handle(new PublishConsentTemplateCommand(code, r.Title, r.Body, r.DisclosedRisks), ct))
            .ToHttp(version => TypedResults.Created($"/api/v1/therapy/modalities/{code}/consent", new { version })));

        admin.MapGet("/practitioner-qualifications", async (
                QualificationStatus? status, IQueryHandler<ListQualificationsQuery, IReadOnlyList<QualificationResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListQualificationsQuery(status), ct)).ToHttp());

        foreach (var decision in Enum.GetValues<QualificationDecision>())
        {
            admin.MapPost($"/practitioner-qualifications/{{qualificationId:guid}}/{decision.ToString().ToLowerInvariant()}", async (
                    Guid qualificationId, DecisionNotes? notes, ICommandHandler<DecideQualificationCommand> handler, CancellationToken ct) =>
                (await handler.Handle(new DecideQualificationCommand(qualificationId, decision, notes?.Notes), ct)).ToHttp());
        }
    }
}
