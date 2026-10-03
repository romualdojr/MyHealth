using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Assessments;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.Nutrition;

public sealed record NutritionAssessmentInput(
    DateOnly AssessedOn,
    string? Goals,
    string? ReportedRestrictions,
    string? ReportedAllergies,
    string? Preferences,
    string? MealTimes,
    IReadOnlyList<MeasurementInput>? Measurements,
    string? FollowUp,
    DateOnly? NextFollowUpOn,
    string? Notes)
{
    internal Result<NutritionAssessmentContent> ToContent()
    {
        var measurements = MeasurementInputs.ToMeasurements(Measurements);
        if (measurements.IsFailure)
        {
            return measurements.Error;
        }

        return new NutritionAssessmentContent(
            AssessedOn, Clean(Goals), Clean(ReportedRestrictions), Clean(ReportedAllergies), Clean(Preferences), Clean(MealTimes), measurements.Value,
            Clean(FollowUp), NextFollowUpOn, Clean(Notes));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class NutritionAssessmentInputValidator : AbstractValidator<NutritionAssessmentInput>
{
    public NutritionAssessmentInputValidator()
    {
        RuleFor(a => a.Goals).MaximumLength(2000);
        RuleFor(a => a.ReportedRestrictions).MaximumLength(2000);
        RuleFor(a => a.ReportedAllergies).MaximumLength(2000);
        RuleFor(a => a.Preferences).MaximumLength(2000);
        RuleFor(a => a.MealTimes).MaximumLength(1000);
        RuleFor(a => a.FollowUp).MaximumLength(2000);
        RuleFor(a => a.Notes).MaximumLength(4000);
        RuleFor(a => a.Measurements).Must(m => m is null || m.Count <= 100).WithMessage("At most 100 measurements per assessment.");
        RuleForEach(a => a.Measurements).SetValidator(new MeasurementInputValidator());
    }
}

public sealed record NutritionAssessmentResponse(
    Guid Id,
    Guid TenantId,
    string OrganizationName,
    Guid ClientId,
    Guid DietitianProfileId,
    string DietitianName,
    NutritionAssessmentContent Content,
    int Version,
    IReadOnlyList<NutritionAssessmentCorrection> Corrections,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A verified dietitian records an assessment for a client linked to them in the active tenant (RF-28).</summary>
public sealed record RecordNutritionAssessmentCommand(Guid ClientId, NutritionAssessmentInput Assessment) : ICommand<Guid>;

internal sealed class RecordNutritionAssessmentValidator : AbstractValidator<RecordNutritionAssessmentCommand>
{
    public RecordNutritionAssessmentValidator()
    {
        RuleFor(c => c.ClientId).NotEmpty();
        RuleFor(c => c.Assessment).NotNull().SetValidator(new NutritionAssessmentInputValidator());
    }
}

internal sealed class RecordNutritionAssessmentHandler(
    ICareAccess access, INutritionAssessmentRepository assessments, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RecordNutritionAssessmentCommand, Guid>
{
    public async Task<Result<Guid>> Handle(RecordNutritionAssessmentCommand command, CancellationToken cancellationToken)
    {
        var dietitian = await access.GetCurrentProfessionalAsync(Discipline.Dietitian, requireVerified: true, cancellationToken);
        if (dietitian.IsFailure)
        {
            return dietitian.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(dietitian.Value, command.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var content = command.Assessment.ToContent();
        if (content.IsFailure)
        {
            return content.Error;
        }

        var assessment = NutritionAssessment.Record(dietitian.Value.TenantId, dietitian.Value.Profile, command.ClientId, content.Value, clock.GetUtcNow());
        if (assessment.IsFailure)
        {
            return assessment.Error;
        }

        assessments.Add(assessment.Value);
        audit.Record(AuditActions.Create, nameof(NutritionAssessment), assessment.Value.Id, command.ClientId, dietitian.Value.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return assessment.Value.Id;
    }
}

public sealed record CorrectNutritionAssessmentCommand(Guid AssessmentId, NutritionAssessmentInput Assessment, string Reason) : ICommand;

internal sealed class CorrectNutritionAssessmentValidator : AbstractValidator<CorrectNutritionAssessmentCommand>
{
    public CorrectNutritionAssessmentValidator()
    {
        RuleFor(c => c.Assessment).NotNull().SetValidator(new NutritionAssessmentInputValidator());
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
    }
}

internal sealed class CorrectNutritionAssessmentHandler(
    ICareAccess access, INutritionAssessmentRepository assessments, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CorrectNutritionAssessmentCommand>
{
    public async Task<Result> Handle(CorrectNutritionAssessmentCommand command, CancellationToken cancellationToken)
    {
        var dietitian = await access.GetCurrentProfessionalAsync(Discipline.Dietitian, requireVerified: true, cancellationToken);
        if (dietitian.IsFailure)
        {
            return dietitian.Error;
        }

        var assessment = await assessments.GetByIdAsync(command.AssessmentId, cancellationToken);
        if (assessment is null || !assessment.IsAuthoredBy(dietitian.Value.Profile.Id) || assessment.TenantId != dietitian.Value.TenantId)
        {
            if (assessment is not null)
            {
                await audit.RecordDeniedAsync(nameof(NutritionAssessment), assessment.Id, dietitian.Value.TenantId, cancellationToken);
            }

            return AssessmentErrors.NotFound;
        }

        var relationship = await access.GetActiveRelationshipAsync(dietitian.Value, assessment.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var content = command.Assessment.ToContent();
        if (content.IsFailure)
        {
            return content.Error;
        }

        var result = assessment.Correct(dietitian.Value.Profile, content.Value, command.Reason, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Correct, nameof(NutritionAssessment), assessment.Id, assessment.ClientId, assessment.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>The dietitian's assessments of a client in the active tenant, while the relationship is active. Audited.</summary>
public sealed record ListClientNutritionAssessmentsQuery(Guid ClientId) : IQuery<IReadOnlyList<NutritionAssessmentResponse>>;

internal sealed class ListClientNutritionAssessmentsHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListClientNutritionAssessmentsQuery, IReadOnlyList<NutritionAssessmentResponse>>
{
    public async Task<Result<IReadOnlyList<NutritionAssessmentResponse>>> Handle(ListClientNutritionAssessmentsQuery query, CancellationToken cancellationToken)
    {
        var dietitian = await access.GetCurrentProfessionalAsync(Discipline.Dietitian, requireVerified: false, cancellationToken);
        if (dietitian.IsFailure)
        {
            return dietitian.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(dietitian.Value, query.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var items = await NutritionAssessmentMapping.ListAsync(
            db,
            db.NutritionAssessments.Where(a => a.ClientId == query.ClientId && a.TenantId == dietitian.Value.TenantId && a.DietitianProfileId == dietitian.Value.Profile.Id),
            cancellationToken);
        await audit.RecordReadAsync("NutritionAssessment.List", relationship.Value.Id, query.ClientId, dietitian.Value.TenantId, cancellationToken);
        return Result.Success(items);
    }
}

/// <summary>Readable by the client, or by the authoring dietitian in its tenant with an active relationship. Others: 404, audited.</summary>
public sealed record GetNutritionAssessmentQuery(Guid AssessmentId) : IQuery<NutritionAssessmentResponse>;

internal sealed class GetNutritionAssessmentHandler(IReadDbContext db, IUserContext user, ITenantContext tenant, IAuditLog audit)
    : IQueryHandler<GetNutritionAssessmentQuery, NutritionAssessmentResponse>
{
    public async Task<Result<NutritionAssessmentResponse>> Handle(GetNutritionAssessmentQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var assessment = await db.NutritionAssessments.FirstOrDefaultAsync(a => a.Id == query.AssessmentId, cancellationToken);
        if (assessment is null)
        {
            return AssessmentErrors.NotFound;
        }

        var single = db.NutritionAssessments.Where(a => a.Id == assessment.Id);
        if (assessment.ClientId == user.UserId)
        {
            return (await NutritionAssessmentMapping.ListAsync(db, single, cancellationToken))[0];
        }

        var activeTenantId = membership.Value?.TenantId;
        var allowed = assessment.DietitianUserId == user.UserId
                      && assessment.TenantId == activeTenantId
                      && membership.Value!.Practices(Discipline.Dietitian)
                      && await db.CareRelationships.AnyAsync(
                          r => r.TenantId == assessment.TenantId && r.ProfessionalProfileId == assessment.DietitianProfileId
                               && r.ClientId == assessment.ClientId && r.Status == RelationshipStatus.Active,
                          cancellationToken);

        if (!allowed)
        {
            await audit.RecordDeniedAsync(nameof(NutritionAssessment), assessment.Id, activeTenantId, cancellationToken);
            return AssessmentErrors.NotFound;
        }

        await audit.RecordReadAsync(nameof(NutritionAssessment), assessment.Id, assessment.ClientId, assessment.TenantId, cancellationToken);
        return (await NutritionAssessmentMapping.ListAsync(db, single, cancellationToken))[0];
    }
}

public sealed record ListMyNutritionAssessmentsQuery : IQuery<IReadOnlyList<NutritionAssessmentResponse>>;

internal sealed class ListMyNutritionAssessmentsHandler(IUserContext user, IReadDbContext db)
    : IQueryHandler<ListMyNutritionAssessmentsQuery, IReadOnlyList<NutritionAssessmentResponse>>
{
    public async Task<Result<IReadOnlyList<NutritionAssessmentResponse>>> Handle(ListMyNutritionAssessmentsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await NutritionAssessmentMapping.ListAsync(db, db.NutritionAssessments.Where(a => a.ClientId == user.UserId), cancellationToken));
}

internal static class NutritionAssessmentMapping
{
    public static async Task<IReadOnlyList<NutritionAssessmentResponse>> ListAsync(
        IReadDbContext db, IQueryable<NutritionAssessment> assessments, CancellationToken cancellationToken)
    {
        var rows = await (
                from a in assessments
                join t in db.Tenants on a.TenantId equals t.Id
                join p in db.ProfessionalProfiles on a.DietitianProfileId equals p.Id
                orderby a.AssessedOn descending, a.CreatedAt descending
                select new { Assessment = a, OrganizationName = t.Name, DietitianName = p.DisplayName })
            .Take(500)
            .ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var a = row.Assessment;
            return new NutritionAssessmentResponse(
                a.Id, a.TenantId, row.OrganizationName, a.ClientId, a.DietitianProfileId, row.DietitianName, a.Content, a.Version, a.Corrections, a.CreatedAt,
                a.UpdatedAt);
        }).ToList();
    }
}
