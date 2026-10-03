using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Auditing;
using MyHealth.Application.CareRelationships;
using MyHealth.Application.Common;
using MyHealth.Application.Tenants;
using MyHealth.Domain.Professionals;

namespace MyHealth.ApiService.Endpoints;

/// <summary>
/// Tenants and memberships (MT-01/02/08). <c>/tenant/*</c> works on the tenant selected with the <c>X-Tenant-Id</c>
/// header; the header is honoured only for an active member of that tenant.
/// </summary>
internal static class TenantEndpoints
{
    public sealed record InviteMemberRequest(string Email, IReadOnlyList<Discipline> Disciplines, bool IsAdministrator);

    public sealed record RevokeMembershipRequest(string? Reason);

    public static void MapTenantEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/tenants", async (CreateTenantCommand command, ICommandHandler<CreateTenantCommand, Guid> handler, CancellationToken ct) =>
                (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created("/api/v1/tenant", new { id })))
            .WithTags("Tenants");

        var current = routes.MapGroup("/tenant").WithTags("Tenants");

        current.MapGet("/", async (IQueryHandler<GetCurrentTenantQuery, CurrentTenantResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetCurrentTenantQuery(), ct)).ToHttp());

        current.MapGet("/memberships", async (IQueryHandler<ListMembershipsQuery, IReadOnlyList<MembershipResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMembershipsQuery(), ct)).ToHttp());

        current.MapPost("/memberships/invitations", async (
                InviteMemberRequest request, ICommandHandler<InviteMemberCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(new InviteMemberCommand(request.Email, request.Disciplines, request.IsAdministrator), ct))
            .ToHttp(id => TypedResults.Created($"/api/v1/tenant/memberships/{id}", new { id })));

        // An administrator removes a member, or a member leaves (their own membership needs no tenant header).
        current.MapPost("/memberships/{membershipId:guid}/revoke", async (
                Guid membershipId, RevokeMembershipRequest? request, ICommandHandler<RevokeMembershipCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RevokeMembershipCommand(membershipId, request?.Reason), ct)).ToHttp());

        current.MapGet("/audit-entries", async (
                Guid? subjectUserId, Guid? actorUserId, int? page, int? pageSize,
                IQueryHandler<ListTenantAuditEntriesQuery, PagedResponse<AuditEntryResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListTenantAuditEntriesQuery(subjectUserId, actorUserId, page ?? 1, pageSize ?? 100), ct)).ToHttp());

        routes.MapGet("/me/tenant-invitations", async (
                    IQueryHandler<ListMyTenantInvitationsQuery, IReadOnlyList<TenantInvitationResponse>> handler, CancellationToken ct) =>
                (await handler.Handle(new ListMyTenantInvitationsQuery(), ct)).ToHttp())
            .WithTags("Tenants");

        var invitations = routes.MapGroup("/tenant-invitations").WithTags("Tenants");

        invitations.MapPost("/{membershipId:guid}/accept", async (
                Guid membershipId, ICommandHandler<RespondToMembershipInvitationCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RespondToMembershipInvitationCommand(membershipId, InvitationResponse.Accept), ct)).ToHttp());

        invitations.MapPost("/{membershipId:guid}/decline", async (
                Guid membershipId, ICommandHandler<RespondToMembershipInvitationCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RespondToMembershipInvitationCommand(membershipId, InvitationResponse.Decline), ct)).ToHttp());
    }
}
