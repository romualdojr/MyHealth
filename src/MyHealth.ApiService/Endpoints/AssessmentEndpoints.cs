using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Assessments;

namespace MyHealth.ApiService.Endpoints;

/// <summary>Trainer side of physical assessments (RF-24). Clients read theirs under <c>/me/physical-assessments</c>.</summary>
internal static class AssessmentEndpoints
{
    public sealed record CorrectPhysicalAssessmentRequest(DateOnly AssessedOn, IReadOnlyList<MeasurementInput> Measurements, string? Notes, string Reason);

    public static void MapAssessmentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/physical-assessments").WithTags("Physical assessments");

        group.MapPost("/", async (
                RecordPhysicalAssessmentCommand command, ICommandHandler<RecordPhysicalAssessmentCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created($"/api/v1/physical-assessments/{id}", new { id })));

        group.MapGet("/", async (
                Guid clientId, IQueryHandler<ListClientPhysicalAssessmentsQuery, IReadOnlyList<PhysicalAssessmentResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListClientPhysicalAssessmentsQuery(clientId), ct)).ToHttp());

        // Readable by the client and by the assessor (in the assessment's tenant).
        group.MapGet("/{assessmentId:guid}", async (
                Guid assessmentId, IQueryHandler<GetPhysicalAssessmentQuery, PhysicalAssessmentResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetPhysicalAssessmentQuery(assessmentId), ct)).ToHttp());

        group.MapPost("/{assessmentId:guid}/corrections", async (
                Guid assessmentId, CorrectPhysicalAssessmentRequest request, ICommandHandler<CorrectPhysicalAssessmentCommand> handler, CancellationToken ct) =>
            (await handler.Handle(
                new CorrectPhysicalAssessmentCommand(assessmentId, request.AssessedOn, request.Measurements, request.Notes, request.Reason), ct)).ToHttp());
    }
}
