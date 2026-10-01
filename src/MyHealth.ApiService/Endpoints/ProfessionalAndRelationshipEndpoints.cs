using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.CareRelationships;
using MyHealth.Application.HealthData;
using MyHealth.Application.Professionals;
using MyHealth.Domain.CareRelationships;

namespace MyHealth.ApiService.Endpoints;

internal static class ProfessionalEndpoints
{
    public sealed record UpdateProfessionalProfileRequest(string DisplayName, string? Bio, string? PhotoUrl, RegistrationInput Registration);

    public static void MapProfessionalEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/professional-profiles").WithTags("Professional profiles");

        group.MapPost("/", async (
                CreateProfessionalProfileCommand command, ICommandHandler<CreateProfessionalProfileCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created($"/api/v1/professional-profiles/{id}", new { id })));

        group.MapPut("/{profileId:guid}", async (
                Guid profileId, UpdateProfessionalProfileRequest request, ICommandHandler<UpdateProfessionalProfileCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new UpdateProfessionalProfileCommand(profileId, request.DisplayName, request.Bio, request.PhotoUrl, request.Registration), ct))
            .ToHttp());
    }
}

internal static class CareRelationshipEndpoints
{
    public sealed record EndRelationshipRequest(string? Reason);

    public static void MapCareRelationshipEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/care-relationships").WithTags("Care relationships");

        group.MapGet("/", async (IQueryHandler<ListMyRelationshipsQuery, IReadOnlyList<CareRelationshipResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyRelationshipsQuery(), ct)).ToHttp());

        group.MapPost("/invitations", async (InviteClientCommand command, ICommandHandler<InviteClientCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created($"/api/v1/care-relationships/{id}", new { id })));

        group.MapPost("/{relationshipId:guid}/accept", async (Guid relationshipId, ICommandHandler<RespondToInvitationCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RespondToInvitationCommand(relationshipId, InvitationResponse.Accept), ct)).ToHttp());

        group.MapPost("/{relationshipId:guid}/decline", async (Guid relationshipId, ICommandHandler<RespondToInvitationCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RespondToInvitationCommand(relationshipId, InvitationResponse.Decline), ct)).ToHttp());

        group.MapPost("/{relationshipId:guid}/end", async (
                Guid relationshipId, EndRelationshipRequest? request, ICommandHandler<EndRelationshipCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new EndRelationshipCommand(relationshipId, request?.Reason), ct)).ToHttp());

        // The client shares (PUT) or stops sharing (DELETE) one data category with this professional.
        group.MapPut("/{relationshipId:guid}/sharing/{category}", async (
                Guid relationshipId, DataCategory category, ICommandHandler<ChangeDataSharingCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeDataSharingCommand(relationshipId, category, Shared: true), ct)).ToHttp());

        group.MapDelete("/{relationshipId:guid}/sharing/{category}", async (
                Guid relationshipId, DataCategory category, ICommandHandler<ChangeDataSharingCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeDataSharingCommand(relationshipId, category, Shared: false), ct)).ToHttp());

        // Professional views of data the client shared through this relationship.
        group.MapGet("/{relationshipId:guid}/heart-rate/sessions", async (
                Guid relationshipId, IQueryHandler<ListSharedHeartRateSessionsQuery, IReadOnlyList<SharedHeartRateSessionItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListSharedHeartRateSessionsQuery(relationshipId), ct)).ToHttp());

        group.MapGet("/{relationshipId:guid}/heart-rate/sessions/{sessionId:guid}", async (
                Guid relationshipId, Guid sessionId, IQueryHandler<GetSharedWorkoutHeartRateQuery, WorkoutHeartRateResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetSharedWorkoutHeartRateQuery(relationshipId, sessionId), ct)).ToHttp());
    }
}
