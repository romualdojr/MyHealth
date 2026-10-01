using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Auditing;
using MyHealth.Application.Common;
using MyHealth.Application.Professionals;
using MyHealth.Domain.Professionals;

namespace MyHealth.ApiService.Endpoints;

/// <summary>Administration (RF-50): professional verification and access audit. No clinical data is exposed here.</summary>
internal static class AdminEndpoints
{
    public sealed record VerifyRequest(string? Notes);

    public sealed record RejectRequest(string Reason);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/admin").WithTags("Administration").RequireAuthorization(AuthorizationPolicies.Administrator);

        group.MapGet("/professional-profiles", async (
                VerificationStatus? status, IQueryHandler<ListProfessionalProfilesQuery, IReadOnlyList<ProfessionalProfileResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListProfessionalProfilesQuery(status), ct)).ToHttp());

        group.MapPost("/professional-profiles/{profileId:guid}/verify", async (
                Guid profileId, VerifyRequest? request, ICommandHandler<VerifyProfessionalCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new VerifyProfessionalCommand(profileId, request?.Notes), ct)).ToHttp());

        group.MapPost("/professional-profiles/{profileId:guid}/reject", async (
                Guid profileId, RejectRequest request, ICommandHandler<RejectProfessionalCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RejectProfessionalCommand(profileId, request.Reason), ct)).ToHttp());

        group.MapGet("/audit-entries", async (
                Guid? subjectUserId, Guid? actorUserId, int? page, int? pageSize,
                IQueryHandler<ListAuditEntriesQuery, PagedResponse<AuditEntryResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListAuditEntriesQuery(subjectUserId, actorUserId, page ?? 1, pageSize ?? 100), ct)).ToHttp());
    }
}
