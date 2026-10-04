using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Administration;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Administration;
using MyHealth.Application.Auditing;
using MyHealth.Application.Common;
using MyHealth.Application.Exercises;
using MyHealth.Application.Privacy;
using MyHealth.Application.Professionals;
using MyHealth.Application.Tenants;
using MyHealth.Domain.Privacy;
using MyHealth.Domain.Professionals;

namespace MyHealth.ApiService.Endpoints;

/// <summary>
/// Platform operation (RF-50): professional verification, tenant list, access audit, LGPD requests, retention
/// configuration, the curated exercise catalog and operator roles. No clinical data is exposed here, the platform role
/// grants no access inside any tenant, and every operator needs two-factor authentication (RNF-02).
/// </summary>
internal static class AdminEndpoints
{
    public sealed record VerifyRequest(string? Notes);

    public sealed record ReasonRequest(string Reason);

    public sealed record PrivacyRequestDecision(string? Notes);

    public sealed record RetentionPolicyRequest(int RetentionMonths, string LegalBasis, string? Notes);

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
                Guid profileId, ReasonRequest request, ICommandHandler<RejectProfessionalCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RejectProfessionalCommand(profileId, request.Reason), ct)).ToHttp());

        group.MapPost("/professional-profiles/{profileId:guid}/suspend", async (
                Guid profileId, ReasonRequest request, ICommandHandler<SuspendProfessionalCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new SuspendProfessionalCommand(profileId, request.Reason), ct)).ToHttp());

        group.MapGet("/audit-entries", async (
                Guid? subjectUserId, Guid? actorUserId, Guid? tenantId, int? page, int? pageSize, string? area,
                IQueryHandler<ListAuditEntriesQuery, PagedResponse<AuditEntryResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListAuditEntriesQuery(subjectUserId, actorUserId, tenantId, page ?? 1, pageSize ?? 100, area), ct)).ToHttp());

        group.MapGet("/tenants", async (IQueryHandler<ListTenantsQuery, IReadOnlyList<TenantSummaryResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListTenantsQuery(), ct)).ToHttp());

        group.MapGet("/privacy-requests", async (
                PrivacyRequestStatus? status, PrivacyRequestType? type,
                IQueryHandler<ListPrivacyRequestsQuery, IReadOnlyList<AdminPrivacyRequestResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListPrivacyRequestsQuery(status, type), ct)).ToHttp());

        foreach (var action in Enum.GetValues<PrivacyRequestAction>())
        {
            group.MapPost($"/privacy-requests/{{requestId:guid}}/{action.ToString().ToLowerInvariant()}", async (
                    Guid requestId, PrivacyRequestDecision? decision, ICommandHandler<HandlePrivacyRequestCommand> handler, CancellationToken ct) =>
                (await handler.Handle(new HandlePrivacyRequestCommand(requestId, action, decision?.Notes), ct)).ToHttp());
        }

        group.MapGet("/retention-policies", async (IQueryHandler<ListRetentionPoliciesQuery, IReadOnlyList<RetentionPolicyResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListRetentionPoliciesQuery(), ct)).ToHttp());

        group.MapPut("/retention-policies/{recordType}", async (
                string recordType, RetentionPolicyRequest request, ICommandHandler<SetRetentionPolicyCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new SetRetentionPolicyCommand(recordType, request.RetentionMonths, request.LegalBasis, request.Notes), ct)).ToHttp());

        group.MapPost("/exercises", async (ExerciseInput exercise, ICommandHandler<CreateSystemExerciseCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(new CreateSystemExerciseCommand(exercise), ct)).ToHttp(id => TypedResults.Created($"/api/v1/exercises/{id}", new { id })));

        group.MapPut("/exercises/{exerciseId:guid}", async (
                Guid exerciseId, ExerciseInput exercise, ICommandHandler<UpdateSystemExerciseCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new UpdateSystemExerciseCommand(exerciseId, exercise), ct)).ToHttp());

        group.MapPut("/exercises/{exerciseId:guid}/media", async (
                Guid exerciseId, ExerciseEndpoints.ExerciseMediaRequest request, ICommandHandler<SetSystemExerciseMediaCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new SetSystemExerciseMediaCommand(exerciseId, request.Media), ct)).ToHttp());

        group.MapPost("/exercises/{exerciseId:guid}/archive", async (Guid exerciseId, ICommandHandler<ArchiveSystemExerciseCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ArchiveSystemExerciseCommand(exerciseId, Archived: true), ct)).ToHttp());

        group.MapPost("/exercises/{exerciseId:guid}/restore", async (Guid exerciseId, ICommandHandler<ArchiveSystemExerciseCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ArchiveSystemExerciseCommand(exerciseId, Archived: false), ct)).ToHttp());

        group.MapGet("/administrators", async (IQueryHandler<ListAdministratorsQuery, IReadOnlyList<AdministratorItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListAdministratorsQuery(), ct)).ToHttp());

        group.MapPut("/administrators/{userId:guid}", async (Guid userId, ICommandHandler<ChangeAdministratorCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeAdministratorCommand(userId, Grant: true), ct)).ToHttp());

        group.MapDelete("/administrators/{userId:guid}", async (Guid userId, ICommandHandler<ChangeAdministratorCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeAdministratorCommand(userId, Grant: false), ct)).ToHttp());
    }
}
