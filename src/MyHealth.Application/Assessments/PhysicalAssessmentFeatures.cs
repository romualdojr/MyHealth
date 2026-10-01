using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.Assessments;

public sealed record MeasurementInput(string Metric, string Method, string Unit, decimal Value);

internal sealed class MeasurementInputValidator : AbstractValidator<MeasurementInput>
{
    public MeasurementInputValidator()
    {
        RuleFor(m => m.Metric).NotEmpty().MaximumLength(64);
        RuleFor(m => m.Method).NotEmpty().MaximumLength(64);
        RuleFor(m => m.Unit).NotEmpty().MaximumLength(16);
    }
}

internal static class MeasurementInputs
{
    public static Result<IReadOnlyList<Measurement>> ToMeasurements(IReadOnlyList<MeasurementInput>? inputs) =>
        Measurement.CreateMany((inputs ?? []).Select(m => (m.Metric, m.Method, m.Unit, m.Value)));
}

public sealed record PhysicalAssessmentResponse(
    Guid Id,
    Guid TenantId,
    string OrganizationName,
    Guid ClientId,
    Guid AssessorProfileId,
    string AssessorName,
    DateOnly AssessedOn,
    IReadOnlyList<Measurement> Measurements,
    string? Notes,
    int Version,
    IReadOnlyList<PhysicalAssessmentCorrection> Corrections,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A verified trainer records an assessment for a client linked to them in the active tenant (RF-24).</summary>
public sealed record RecordPhysicalAssessmentCommand(Guid ClientId, DateOnly AssessedOn, IReadOnlyList<MeasurementInput> Measurements, string? Notes)
    : ICommand<Guid>;

internal sealed class RecordPhysicalAssessmentValidator : AbstractValidator<RecordPhysicalAssessmentCommand>
{
    public RecordPhysicalAssessmentValidator()
    {
        RuleFor(c => c.ClientId).NotEmpty();
        RuleFor(c => c.Measurements).NotNull().Must(m => m.Count <= 100).WithMessage("At most 100 measurements per assessment.");
        RuleForEach(c => c.Measurements).SetValidator(new MeasurementInputValidator());
        RuleFor(c => c.Notes).MaximumLength(4000);
    }
}

internal sealed class RecordPhysicalAssessmentHandler(
    ICareAccess access, IPhysicalAssessmentRepository assessments, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RecordPhysicalAssessmentCommand, Guid>
{
    public async Task<Result<Guid>> Handle(RecordPhysicalAssessmentCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(trainer.Value, command.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var measurements = MeasurementInputs.ToMeasurements(command.Measurements);
        if (measurements.IsFailure)
        {
            return measurements.Error;
        }

        var assessment = PhysicalAssessment.Record(
            trainer.Value.TenantId, trainer.Value.Profile, command.ClientId, command.AssessedOn, measurements.Value, command.Notes, clock.GetUtcNow());
        if (assessment.IsFailure)
        {
            return assessment.Error;
        }

        assessments.Add(assessment.Value);
        audit.Record(AuditActions.Create, nameof(PhysicalAssessment), assessment.Value.Id, command.ClientId, trainer.Value.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return assessment.Value.Id;
    }
}

/// <summary>The assessor corrects an assessment; the replaced values are kept with the reason (§5 History).</summary>
public sealed record CorrectPhysicalAssessmentCommand(
    Guid AssessmentId, DateOnly AssessedOn, IReadOnlyList<MeasurementInput> Measurements, string? Notes, string Reason) : ICommand;

internal sealed class CorrectPhysicalAssessmentValidator : AbstractValidator<CorrectPhysicalAssessmentCommand>
{
    public CorrectPhysicalAssessmentValidator()
    {
        RuleFor(c => c.Measurements).NotNull().Must(m => m.Count <= 100).WithMessage("At most 100 measurements per assessment.");
        RuleForEach(c => c.Measurements).SetValidator(new MeasurementInputValidator());
        RuleFor(c => c.Notes).MaximumLength(4000);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
    }
}

internal sealed class CorrectPhysicalAssessmentHandler(
    ICareAccess access, IPhysicalAssessmentRepository assessments, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CorrectPhysicalAssessmentCommand>
{
    public async Task<Result> Handle(CorrectPhysicalAssessmentCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var assessment = await assessments.GetByIdAsync(command.AssessmentId, cancellationToken);
        if (assessment is null || !assessment.IsAuthoredBy(trainer.Value.Profile.Id) || assessment.TenantId != trainer.Value.TenantId)
        {
            if (assessment is not null)
            {
                await audit.RecordDeniedAsync(nameof(PhysicalAssessment), assessment.Id, trainer.Value.TenantId, cancellationToken);
            }

            return AssessmentErrors.NotFound;
        }

        var relationship = await access.GetActiveRelationshipAsync(trainer.Value, assessment.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var measurements = MeasurementInputs.ToMeasurements(command.Measurements);
        if (measurements.IsFailure)
        {
            return measurements.Error;
        }

        var result = assessment.Correct(trainer.Value.Profile, command.AssessedOn, measurements.Value, command.Notes, command.Reason, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Correct, nameof(PhysicalAssessment), assessment.Id, assessment.ClientId, assessment.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>Assessments the trainer recorded for a client in the active tenant, while the relationship is active.</summary>
public sealed record ListClientPhysicalAssessmentsQuery(Guid ClientId) : IQuery<IReadOnlyList<PhysicalAssessmentResponse>>;

internal sealed class ListClientPhysicalAssessmentsHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListClientPhysicalAssessmentsQuery, IReadOnlyList<PhysicalAssessmentResponse>>
{
    public async Task<Result<IReadOnlyList<PhysicalAssessmentResponse>>> Handle(ListClientPhysicalAssessmentsQuery query, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(trainer.Value, query.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var items = await PhysicalAssessmentMapping.ListAsync(
            db,
            db.PhysicalAssessments.Where(a => a.ClientId == query.ClientId && a.TenantId == trainer.Value.TenantId && a.AssessorProfileId == trainer.Value.Profile.Id),
            cancellationToken);
        await audit.RecordReadAsync("PhysicalAssessment.List", relationship.Value.Id, query.ClientId, trainer.Value.TenantId, cancellationToken);
        return Result.Success(items);
    }
}

/// <summary>
/// One assessment: readable by the client, or by its assessor while acting for its tenant with an active relationship.
/// Anyone else gets "not found", and the attempt is audited (MT-09).
/// </summary>
public sealed record GetPhysicalAssessmentQuery(Guid AssessmentId) : IQuery<PhysicalAssessmentResponse>;

internal sealed class GetPhysicalAssessmentHandler(IReadDbContext db, IUserContext user, ITenantContext tenant, IAuditLog audit)
    : IQueryHandler<GetPhysicalAssessmentQuery, PhysicalAssessmentResponse>
{
    public async Task<Result<PhysicalAssessmentResponse>> Handle(GetPhysicalAssessmentQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var assessment = await db.PhysicalAssessments.FirstOrDefaultAsync(a => a.Id == query.AssessmentId, cancellationToken);
        if (assessment is null)
        {
            return AssessmentErrors.NotFound;
        }

        if (assessment.ClientId == user.UserId)
        {
            return (await PhysicalAssessmentMapping.ListAsync(db, db.PhysicalAssessments.Where(a => a.Id == assessment.Id), cancellationToken))[0];
        }

        var activeTenantId = membership.Value?.TenantId;
        var allowed = assessment.AssessorUserId == user.UserId
                      && assessment.TenantId == activeTenantId
                      && membership.Value!.Practices(Discipline.PersonalTrainer)
                      && await db.CareRelationships.AnyAsync(
                          r => r.TenantId == assessment.TenantId && r.ProfessionalProfileId == assessment.AssessorProfileId
                               && r.ClientId == assessment.ClientId && r.Status == RelationshipStatus.Active,
                          cancellationToken);

        if (!allowed)
        {
            await audit.RecordDeniedAsync(nameof(PhysicalAssessment), assessment.Id, activeTenantId, cancellationToken);
            return AssessmentErrors.NotFound;
        }

        await audit.RecordReadAsync(nameof(PhysicalAssessment), assessment.Id, assessment.ClientId, assessment.TenantId, cancellationToken);
        return (await PhysicalAssessmentMapping.ListAsync(db, db.PhysicalAssessments.Where(a => a.Id == assessment.Id), cancellationToken))[0];
    }
}

/// <summary>The client's assessments from every organization, labelled by organization and assessor (MT-10).</summary>
public sealed record ListMyPhysicalAssessmentsQuery : IQuery<IReadOnlyList<PhysicalAssessmentResponse>>;

internal sealed class ListMyPhysicalAssessmentsHandler(IUserContext user, IReadDbContext db)
    : IQueryHandler<ListMyPhysicalAssessmentsQuery, IReadOnlyList<PhysicalAssessmentResponse>>
{
    public async Task<Result<IReadOnlyList<PhysicalAssessmentResponse>>> Handle(ListMyPhysicalAssessmentsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await PhysicalAssessmentMapping.ListAsync(db, db.PhysicalAssessments.Where(a => a.ClientId == user.UserId), cancellationToken));
}

/// <summary>
/// Assessments shared with a dietitian, practitioner or another trainer through a <c>PhysicalAssessments</c> grant (§6
/// "view if authorized"). Only assessments recorded in the relationship's organization are visible. Audited.
/// </summary>
public sealed record ListSharedPhysicalAssessmentsQuery(Guid RelationshipId) : IQuery<IReadOnlyList<PhysicalAssessmentResponse>>;

internal sealed class ListSharedPhysicalAssessmentsHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListSharedPhysicalAssessmentsQuery, IReadOnlyList<PhysicalAssessmentResponse>>
{
    public async Task<Result<IReadOnlyList<PhysicalAssessmentResponse>>> Handle(ListSharedPhysicalAssessmentsQuery query, CancellationToken cancellationToken)
    {
        var relationship = await access.GetSharedAccessAsync(query.RelationshipId, DataCategory.PhysicalAssessments, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var r = relationship.Value;
        var items = await PhysicalAssessmentMapping.ListAsync(
            db, db.PhysicalAssessments.Where(a => a.ClientId == r.ClientId && a.TenantId == r.TenantId), cancellationToken);
        await audit.RecordReadAsync("PhysicalAssessment.List", r.Id, r.ClientId, r.TenantId, cancellationToken);
        return Result.Success(items);
    }
}

internal static class PhysicalAssessmentMapping
{
    public static async Task<IReadOnlyList<PhysicalAssessmentResponse>> ListAsync(
        IReadDbContext db, IQueryable<PhysicalAssessment> assessments, CancellationToken cancellationToken)
    {
        var rows = await (
                from a in assessments
                join t in db.Tenants on a.TenantId equals t.Id
                join p in db.ProfessionalProfiles on a.AssessorProfileId equals p.Id
                orderby a.AssessedOn descending, a.CreatedAt descending
                select new { Assessment = a, OrganizationName = t.Name, AssessorName = p.DisplayName })
            .Take(500)
            .ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var a = row.Assessment;
            return new PhysicalAssessmentResponse(
                a.Id, a.TenantId, row.OrganizationName, a.ClientId, a.AssessorProfileId, row.AssessorName, a.AssessedOn, a.Measurements, a.Notes, a.Version,
                a.Corrections, a.CreatedAt, a.UpdatedAt);
        }).ToList();
    }
}
