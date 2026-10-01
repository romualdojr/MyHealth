using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.Nutrition;

public sealed record MealPlanDetailsInput(string Name, string? Goal, DateOnly StartDate, DateOnly? EndDate, string? Notes)
{
    internal MealPlanDetails ToDetails() =>
        new(Name.Trim(), string.IsNullOrWhiteSpace(Goal) ? null : Goal.Trim(), StartDate, EndDate, string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim());
}

/// <summary>Send back the existing ids when editing so diary entries stay linked to the same meal across versions.</summary>
public sealed record MealItemInput(
    Guid? Id, string Food, decimal? Quantity, string? Unit, string? HouseholdMeasure, string? Notes, IReadOnlyList<string>? Substitutions);

public sealed record PlannedMealInput(Guid? Id, string Name, TimeOnly? Time, string? Notes, IReadOnlyList<MealItemInput> Items);

internal sealed class MealPlanDetailsInputValidator : AbstractValidator<MealPlanDetailsInput>
{
    public MealPlanDetailsInputValidator()
    {
        RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
        RuleFor(d => d.Goal).MaximumLength(500);
        RuleFor(d => d.Notes).MaximumLength(4000);
        RuleFor(d => d.EndDate).GreaterThanOrEqualTo(d => d.StartDate).When(d => d.EndDate is not null);
    }
}

internal sealed class PlannedMealInputValidator : AbstractValidator<PlannedMealInput>
{
    public PlannedMealInputValidator()
    {
        RuleFor(m => m.Name).NotEmpty().MaximumLength(200);
        RuleFor(m => m.Notes).MaximumLength(2000);
        RuleFor(m => m.Items).NotNull().Must(i => i.Count <= 50).WithMessage("A meal can have at most 50 foods.");
        RuleForEach(m => m.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Food).NotEmpty().MaximumLength(200);
            item.RuleFor(i => i.Quantity).GreaterThan(0).LessThanOrEqualTo(100_000).When(i => i.Quantity is not null);
            item.RuleFor(i => i.Unit).MaximumLength(20);
            item.RuleFor(i => i.HouseholdMeasure).MaximumLength(200);
            item.RuleFor(i => i.Notes).MaximumLength(1000);
            item.RuleFor(i => i.Substitutions).Must(s => s is null || (s.Count <= 20 && s.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 200)))
                .WithMessage("Up to 20 substitutions of at most 200 characters each.");
        });
    }
}

/// <summary>Creates a draft in the active tenant for a client the dietitian is linked to there. Only verified dietitians.</summary>
public sealed record CreateMealPlanCommand(Guid ClientId, MealPlanDetailsInput Details) : ICommand<Guid>;

internal sealed class CreateMealPlanValidator : AbstractValidator<CreateMealPlanCommand>
{
    public CreateMealPlanValidator()
    {
        RuleFor(c => c.ClientId).NotEmpty();
        RuleFor(c => c.Details).NotNull().SetValidator(new MealPlanDetailsInputValidator());
    }
}

internal sealed class CreateMealPlanHandler(ICareAccess access, IMealPlanRepository plans, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateMealPlanCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateMealPlanCommand command, CancellationToken cancellationToken)
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

        var plan = MealPlan.Create(dietitian.Value.TenantId, dietitian.Value.Profile, command.ClientId, command.Details.ToDetails(), clock.GetUtcNow());
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        plans.Add(plan.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return plan.Value.Id;
    }
}

public sealed record UpdateMealPlanDetailsCommand(Guid PlanId, MealPlanDetailsInput Details) : ICommand;

internal sealed class UpdateMealPlanDetailsValidator : AbstractValidator<UpdateMealPlanDetailsCommand>
{
    public UpdateMealPlanDetailsValidator() => RuleFor(c => c.Details).NotNull().SetValidator(new MealPlanDetailsInputValidator());
}

internal sealed class UpdateMealPlanDetailsHandler(
    ICareAccess access, IMealPlanRepository plans, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<UpdateMealPlanDetailsCommand>
{
    public async Task<Result> Handle(UpdateMealPlanDetailsCommand command, CancellationToken cancellationToken)
    {
        var loaded = await DietitianPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var result = loaded.Value.Plan.UpdateDetails(command.Details.ToDetails(), clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>Replaces the draft meals (RF-29: meals and times, foods and portions, substitutions, notes).</summary>
public sealed record UpdateMealPlanMealsCommand(Guid PlanId, IReadOnlyList<PlannedMealInput> Meals) : ICommand;

internal sealed class UpdateMealPlanMealsValidator : AbstractValidator<UpdateMealPlanMealsCommand>
{
    public UpdateMealPlanMealsValidator()
    {
        RuleFor(c => c.Meals).NotNull().Must(m => m.Count <= 20).WithMessage("A meal plan can have at most 20 meals.");
        RuleForEach(c => c.Meals).SetValidator(new PlannedMealInputValidator());
    }
}

internal sealed class UpdateMealPlanMealsHandler(
    ICareAccess access, IMealPlanRepository plans, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<UpdateMealPlanMealsCommand>
{
    public async Task<Result> Handle(UpdateMealPlanMealsCommand command, CancellationToken cancellationToken)
    {
        var loaded = await DietitianPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var now = clock.GetUtcNow();
        var content = new MealPlanContent(command.Meals
            .Select((meal, index) => new PlannedMeal(
                meal.Id ?? Guid.CreateVersion7(now),
                meal.Name.Trim(),
                meal.Time,
                index + 1,
                string.IsNullOrWhiteSpace(meal.Notes) ? null : meal.Notes.Trim(),
                meal.Items.Select(item => new MealItem(
                    item.Id ?? Guid.CreateVersion7(now),
                    item.Food.Trim(),
                    item.Quantity,
                    string.IsNullOrWhiteSpace(item.Unit) ? null : item.Unit.Trim(),
                    string.IsNullOrWhiteSpace(item.HouseholdMeasure) ? null : item.HouseholdMeasure.Trim(),
                    string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes.Trim(),
                    (item.Substitutions ?? []).Select(s => s.Trim()).ToList())).ToList()))
            .OrderBy(m => m.Time ?? TimeOnly.MaxValue)
            .Select((meal, index) => meal with { Order = index + 1 })
            .ToList());

        var result = loaded.Value.Plan.UpdateDraft(content, now);
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record PublishedMealPlanVersionResponse(Guid VersionId, int VersionNumber, DateTimeOffset PublishedAt);

/// <summary>Freezes the draft into a new version and notifies the client (RF-29). Only a verified dietitian can prescribe.</summary>
public sealed record PublishMealPlanCommand(Guid PlanId, string ChangeDescription) : ICommand<PublishedMealPlanVersionResponse>;

internal sealed class PublishMealPlanValidator : AbstractValidator<PublishMealPlanCommand>
{
    public PublishMealPlanValidator() => RuleFor(c => c.ChangeDescription).NotEmpty().MaximumLength(1000);
}

internal sealed class PublishMealPlanHandler(
    ICareAccess access, IMealPlanRepository plans, IMealPlanVersionRepository versions, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<PublishMealPlanCommand, PublishedMealPlanVersionResponse>
{
    public async Task<Result<PublishedMealPlanVersionResponse>> Handle(PublishMealPlanCommand command, CancellationToken cancellationToken)
    {
        var loaded = await DietitianPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (dietitian, plan) = loaded.Value;
        var relationship = await access.GetActiveRelationshipAsync(dietitian, plan.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var version = plan.Publish(dietitian.Profile, command.ChangeDescription, clock.GetUtcNow());
        if (version.IsFailure)
        {
            return version.Error;
        }

        versions.Add(version.Value);
        audit.Record(AuditActions.Publish, nameof(MealPlanVersion), version.Value.Id, plan.ClientId, plan.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new PublishedMealPlanVersionResponse(version.Value.Id, version.Value.VersionNumber, version.Value.PublishedAt);
    }
}

public sealed record EndMealPlanCommand(Guid PlanId) : ICommand;

internal sealed class EndMealPlanHandler(ICareAccess access, IMealPlanRepository plans, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<EndMealPlanCommand>
{
    public async Task<Result> Handle(EndMealPlanCommand command, CancellationToken cancellationToken)
    {
        var loaded = await DietitianPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var result = loaded.Value.Plan.End(clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.End, nameof(MealPlan), loaded.Value.Plan.Id, loaded.Value.Plan.ClientId, loaded.Value.Plan.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record MealPlanVersionSummary(Guid VersionId, int VersionNumber, string ChangeDescription, string AuthorName, DateTimeOffset PublishedAt);

public sealed record DietitianMealPlanResponse(
    Guid Id,
    Guid ClientId,
    MealPlanDetails Details,
    MealPlanStatus Status,
    MealPlanContent Draft,
    bool HasUnpublishedChanges,
    int CurrentVersionNumber,
    IReadOnlyList<MealPlanVersionSummary> Versions);

/// <summary>
/// The dietitian's full view of their plan, including the unpublished draft, while the relationship with the client is
/// active. Reads are audited; a known id outside the dietitian's scope is "not found" and audited as denied (MT-09).
/// </summary>
public sealed record GetDietitianMealPlanQuery(Guid PlanId) : IQuery<DietitianMealPlanResponse>;

internal sealed class GetDietitianMealPlanHandler(IReadDbContext db, ICareAccess access, IAuditLog audit)
    : IQueryHandler<GetDietitianMealPlanQuery, DietitianMealPlanResponse>
{
    public async Task<Result<DietitianMealPlanResponse>> Handle(GetDietitianMealPlanQuery query, CancellationToken cancellationToken)
    {
        var dietitian = await access.GetCurrentProfessionalAsync(Discipline.Dietitian, requireVerified: false, cancellationToken);
        if (dietitian.IsFailure)
        {
            return dietitian.Error;
        }

        var plan = await db.MealPlans.FirstOrDefaultAsync(p => p.Id == query.PlanId, cancellationToken);
        if (plan is null || plan.DietitianProfileId != dietitian.Value.Profile.Id || plan.TenantId != dietitian.Value.TenantId)
        {
            if (plan is not null)
            {
                await audit.RecordDeniedAsync(nameof(MealPlan), plan.Id, dietitian.Value.TenantId, cancellationToken);
            }

            return MealPlanErrors.NotFound;
        }

        var relationship = await access.GetActiveRelationshipAsync(dietitian.Value, plan.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        await audit.RecordReadAsync(nameof(MealPlan), plan.Id, plan.ClientId, plan.TenantId, cancellationToken);
        return new DietitianMealPlanResponse(
            plan.Id, plan.ClientId, plan.Details, plan.Status, plan.Draft, plan.HasUnpublishedChanges, plan.CurrentVersionNumber,
            await MealPlanMapping.VersionsAsync(db, plan.Id, cancellationToken));
    }
}

public sealed record DietitianMealPlanListItem(
    Guid Id, Guid ClientId, string Name, MealPlanStatus Status, int CurrentVersionNumber, bool HasUnpublishedChanges, DateTimeOffset UpdatedAt);

public sealed record ListDietitianMealPlansQuery(Guid? ClientId) : IQuery<IReadOnlyList<DietitianMealPlanListItem>>;

internal sealed class ListDietitianMealPlansHandler(IReadDbContext db, ICareAccess access)
    : IQueryHandler<ListDietitianMealPlansQuery, IReadOnlyList<DietitianMealPlanListItem>>
{
    public async Task<Result<IReadOnlyList<DietitianMealPlanListItem>>> Handle(ListDietitianMealPlansQuery query, CancellationToken cancellationToken)
    {
        var dietitian = await access.GetCurrentProfessionalAsync(Discipline.Dietitian, requireVerified: false, cancellationToken);
        if (dietitian.IsFailure)
        {
            return dietitian.Error;
        }

        var plans = db.MealPlans.Where(p => p.DietitianProfileId == dietitian.Value.Profile.Id && p.TenantId == dietitian.Value.TenantId);
        if (query.ClientId is { } clientId)
        {
            plans = plans.Where(p => p.ClientId == clientId);
        }

        var items = await plans
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new DietitianMealPlanListItem(p.Id, p.ClientId, p.Details.Name, p.Status, p.CurrentVersionNumber, p.HasUnpublishedChanges, p.UpdatedAt))
            .ToListAsync(cancellationToken);
        return items;
    }
}


public sealed record MealPlanVersionResponse(
    Guid PlanId, Guid VersionId, int VersionNumber, MealPlanDetails Details, MealPlanContent Content, string ChangeDescription, string AuthorName,
    DateTimeOffset PublishedAt);

/// <summary>
/// Any published version: readable by its client, or by the authoring dietitian while acting for the plan's tenant with
/// an active relationship (audited). Anyone else gets "not found", and the attempt is audited (MT-09).
/// </summary>
public sealed record GetMealPlanVersionQuery(Guid PlanId, int VersionNumber) : IQuery<MealPlanVersionResponse>;

internal sealed class GetMealPlanVersionHandler(IReadDbContext db, IUserContext user, ITenantContext tenant, IAuditLog audit)
    : IQueryHandler<GetMealPlanVersionQuery, MealPlanVersionResponse>
{
    public async Task<Result<MealPlanVersionResponse>> Handle(GetMealPlanVersionQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var row = await (
                from version in db.MealPlanVersions
                join p in db.ProfessionalProfiles on version.DietitianProfileId equals p.Id
                where version.MealPlanId == query.PlanId && version.VersionNumber == query.VersionNumber
                select new { Version = version, AuthorName = p.DisplayName, Author = p })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return MealPlanErrors.VersionNotFound;
        }

        var v = row.Version;
        var response = new MealPlanVersionResponse(v.MealPlanId, v.Id, v.VersionNumber, v.Details, v.Content, v.ChangeDescription, row.AuthorName, v.PublishedAt);
        if (v.ClientId == user.UserId)
        {
            return response;
        }

        var activeTenantId = membership.Value?.TenantId;
        var allowed = v.DietitianUserId == user.UserId
                      && v.TenantId == activeTenantId
                      && membership.Value!.Practices(Discipline.Dietitian)
                      && !row.Author.IsBarred
                      && await db.CareRelationships.AnyAsync(
                          r => r.TenantId == v.TenantId && r.ProfessionalProfileId == v.DietitianProfileId && r.ClientId == v.ClientId
                               && r.Status == RelationshipStatus.Active,
                          cancellationToken);

        if (!allowed)
        {
            await audit.RecordDeniedAsync(nameof(MealPlanVersion), v.Id, activeTenantId, cancellationToken);
            return MealPlanErrors.VersionNotFound;
        }

        await audit.RecordReadAsync(nameof(MealPlanVersion), v.Id, v.ClientId, v.TenantId, cancellationToken);
        return response;
    }
}

/// <summary>A meal plan in the client's view, labelled with organization and dietitian (MT-10). Drafts are never shown.</summary>
public sealed record ClientMealPlanListItem(
    Guid PlanId, Guid TenantId, string OrganizationName, string DietitianName, string Name, string? Goal, DateOnly StartDate, DateOnly? EndDate,
    MealPlanStatus Status, int VersionNumber, DateTimeOffset PublishedAt);

public sealed record ListMyMealPlansQuery : IQuery<IReadOnlyList<ClientMealPlanListItem>>;

internal sealed class ListMyMealPlansHandler(IReadDbContext db, IUserContext user) : IQueryHandler<ListMyMealPlansQuery, IReadOnlyList<ClientMealPlanListItem>>
{
    public async Task<Result<IReadOnlyList<ClientMealPlanListItem>>> Handle(ListMyMealPlansQuery query, CancellationToken cancellationToken)
    {
        var items = await (
                from plan in db.MealPlans
                join version in db.MealPlanVersions on plan.CurrentVersionId equals version.Id
                join tenant in db.Tenants on plan.TenantId equals tenant.Id
                join dietitian in db.ProfessionalProfiles on plan.DietitianProfileId equals dietitian.Id
                where plan.ClientId == user.UserId
                orderby plan.Status, version.Details.StartDate descending
                select new ClientMealPlanListItem(
                    plan.Id, plan.TenantId, tenant.Name, dietitian.DisplayName, version.Details.Name, version.Details.Goal, version.Details.StartDate,
                    version.Details.EndDate, plan.Status, version.VersionNumber, version.PublishedAt))
            .ToListAsync(cancellationToken);
        return items;
    }
}

public sealed record ClientMealPlanResponse(
    Guid PlanId,
    Guid TenantId,
    string OrganizationName,
    string DietitianName,
    MealPlanStatus Status,
    MealPlanVersionResponse Current,
    IReadOnlyList<MealPlanVersionSummary> History);

/// <summary>The current published version and the full version history (acceptance test 6).</summary>
public sealed record GetMyMealPlanQuery(Guid PlanId) : IQuery<ClientMealPlanResponse>;

internal sealed class GetMyMealPlanHandler(IReadDbContext db, IUserContext user) : IQueryHandler<GetMyMealPlanQuery, ClientMealPlanResponse>
{
    public async Task<Result<ClientMealPlanResponse>> Handle(GetMyMealPlanQuery query, CancellationToken cancellationToken)
    {
        var row = await (
                from plan in db.MealPlans
                join version in db.MealPlanVersions on plan.CurrentVersionId equals version.Id
                join tenant in db.Tenants on plan.TenantId equals tenant.Id
                join dietitian in db.ProfessionalProfiles on version.DietitianProfileId equals dietitian.Id
                where plan.Id == query.PlanId && plan.ClientId == user.UserId
                select new { plan.Status, plan.TenantId, OrganizationName = tenant.Name, DietitianName = dietitian.DisplayName, Version = version })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return MealPlanErrors.NotFound;
        }

        var v = row.Version;
        return new ClientMealPlanResponse(
            v.MealPlanId, row.TenantId, row.OrganizationName, row.DietitianName, row.Status,
            new MealPlanVersionResponse(v.MealPlanId, v.Id, v.VersionNumber, v.Details, v.Content, v.ChangeDescription, row.DietitianName, v.PublishedAt),
            await MealPlanMapping.VersionsAsync(db, v.MealPlanId, cancellationToken));
    }
}

internal static class MealPlanMapping
{
    public static async Task<IReadOnlyList<MealPlanVersionSummary>> VersionsAsync(IReadDbContext db, Guid planId, CancellationToken cancellationToken) =>
        await (
                from v in db.MealPlanVersions
                join p in db.ProfessionalProfiles on v.DietitianProfileId equals p.Id
                where v.MealPlanId == planId
                orderby v.VersionNumber descending
                select new MealPlanVersionSummary(v.Id, v.VersionNumber, v.ChangeDescription, p.DisplayName, v.PublishedAt))
            .ToListAsync(cancellationToken);
}

internal static class DietitianPlanLoader
{
    /// <summary>
    /// Loads a meal plan only if it belongs to the calling dietitian in the active tenant; otherwise "not found". A known
    /// id outside that scope is audited as a denied attempt (MT-09).
    /// </summary>
    public static async Task<Result<(ActingProfessional Dietitian, MealPlan Plan)>> LoadOwnedAsync(
        ICareAccess access, IMealPlanRepository plans, IAuditLog audit, Guid planId, CancellationToken cancellationToken)
    {
        var dietitian = await access.GetCurrentProfessionalAsync(Discipline.Dietitian, requireVerified: true, cancellationToken);
        if (dietitian.IsFailure)
        {
            return dietitian.Error;
        }

        var plan = await plans.GetByIdAsync(planId, cancellationToken);
        if (plan is null || !plan.IsOwnedBy(dietitian.Value.Profile.Id) || plan.TenantId != dietitian.Value.TenantId)
        {
            if (plan is not null)
            {
                await audit.RecordDeniedAsync(nameof(MealPlan), planId, dietitian.Value.TenantId, cancellationToken);
            }

            return MealPlanErrors.NotFound;
        }

        return (dietitian.Value, plan);
    }
}
