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
using MyHealth.Domain.Users;

namespace MyHealth.Application.Nutrition;

/// <summary>
/// The client saves a diary entry (RF-30). The id comes from the phone, so retries and offline sync are safe upserts.
/// Pass <paramref name="MealPlanId"/> (and optionally <paramref name="PlannedMealId"/>) to log against a plan, or
/// <paramref name="RelationshipId"/> to share an unplanned entry with a dietitian. With neither, the entry is private.
/// </summary>
public sealed record SaveFoodDiaryEntryCommand(
    Guid EntryId,
    Guid? MealPlanId,
    Guid? PlannedMealId,
    Guid? RelationshipId,
    string? MealName,
    DateTimeOffset EatenAt,
    string? TimeZoneId,
    string? Description,
    int? WaterMl,
    MealAdherence? Adherence,
    string? ClientNotes) : ICommand;

internal sealed class SaveFoodDiaryEntryValidator : AbstractValidator<SaveFoodDiaryEntryCommand>
{
    public SaveFoodDiaryEntryValidator()
    {
        RuleFor(c => c.EntryId).NotEmpty();
        RuleFor(c => c.MealName).MaximumLength(100);
        RuleFor(c => c.Description).MaximumLength(2000);
        RuleFor(c => c.ClientNotes).MaximumLength(2000);
        RuleFor(c => c.Adherence).IsInEnum().When(c => c.Adherence is not null);
        RuleFor(c => c.TimeZoneId).Must(TimeZones.IsValid).When(c => c.TimeZoneId is not null).WithMessage("Use an IANA time zone such as America/Sao_Paulo.");
        RuleFor(c => c.PlannedMealId).Null().When(c => c.MealPlanId is null).WithMessage("A planned meal needs its meal plan.");
        RuleFor(c => c.RelationshipId).Null().When(c => c.MealPlanId is not null).WithMessage("Send either a meal plan or a relationship, not both.");
    }
}

internal sealed class SaveFoodDiaryEntryHandler(IUserContext user, IReadDbContext db, IFoodDiaryRepository entries, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SaveFoodDiaryEntryCommand>
{
    public async Task<Result> Handle(SaveFoodDiaryEntryCommand command, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == user.UserId, cancellationToken);
        if (profile is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var addressee = await ResolveAddresseeAsync(command, cancellationToken);
        if (addressee.IsFailure)
        {
            return addressee.Error;
        }

        var now = clock.GetUtcNow();
        var details = new FoodDiaryDetails(
            command.MealName, command.EatenAt, command.TimeZoneId ?? profile.TimeZoneId, command.Description, command.WaterMl, command.Adherence,
            command.ClientNotes);

        var existing = await entries.GetByIdAsync(command.EntryId, cancellationToken);
        if (existing is not null)
        {
            if (!existing.HasSameAddressee(user.UserId, addressee.Value))
            {
                return FoodDiaryErrors.IdConflict;
            }

            var revised = existing.Revise(user.UserId, details, now);
            if (revised.IsSuccess)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return revised;
        }

        var entry = FoodDiaryEntry.Record(command.EntryId, user.UserId, addressee.Value, details, now);
        if (entry.IsFailure)
        {
            return entry.Error;
        }

        entries.Add(entry.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>The tenant and dietitian always come from the server's own records, never from client input (MT-04).</summary>
    private async Task<Result<DiaryAddressee?>> ResolveAddresseeAsync(SaveFoodDiaryEntryCommand command, CancellationToken cancellationToken)
    {
        if (command.MealPlanId is { } planId)
        {
            var row = await (
                    from plan in db.MealPlans
                    join version in db.MealPlanVersions on plan.CurrentVersionId equals version.Id
                    where plan.Id == planId && plan.ClientId == user.UserId
                    select new { plan.TenantId, plan.DietitianProfileId, version.VersionNumber, version.Content })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return MealPlanErrors.NotFound;
            }

            if (command.PlannedMealId is { } mealId && row.Content.FindMeal(mealId) is null)
            {
                return FoodDiaryErrors.PlannedMealNotFound;
            }

            return new DiaryAddressee(row.TenantId, row.DietitianProfileId, planId, row.VersionNumber, command.PlannedMealId);
        }

        if (command.RelationshipId is { } relationshipId)
        {
            var relationship = await db.CareRelationships.FirstOrDefaultAsync(
                r => r.Id == relationshipId && r.ClientId == user.UserId && r.Discipline == Discipline.Dietitian && r.Status == RelationshipStatus.Active,
                cancellationToken);

            return relationship is null
                ? FoodDiaryErrors.NoDietitianRelationship
                : new DiaryAddressee(relationship.TenantId, relationship.ProfessionalProfileId, null, null, null);
        }

        return Result.Success<DiaryAddressee?>(null);
    }
}

public sealed record FoodDiaryCommentResponse(Guid Id, string AuthorName, string Message, DateTimeOffset CreatedAt);

public sealed record FoodDiaryEntryResponse(
    Guid Id,
    Guid? TenantId,
    string? OrganizationName,
    Guid? MealPlanId,
    int? MealPlanVersionNumber,
    Guid? PlannedMealId,
    string? MealName,
    DateTimeOffset EatenAt,
    DateOnly LocalDate,
    string? Description,
    int? WaterMl,
    MealAdherence? Adherence,
    string? ClientNotes,
    IReadOnlyList<FoodDiaryCommentResponse> Comments,
    DateTimeOffset RecordedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Counts only: how meals related to the plan and how much water was logged. Never food contents.</summary>
public sealed record DiaryAdherenceSummary(
    DateOnly From, DateOnly To, int Entries, int DaysWithEntries, int AsPlanned, int Partly, int Different, int Unplanned, int NotStated, int WaterMl);

public sealed record DietitianFoodDiaryResponse(IReadOnlyList<FoodDiaryEntryResponse> Entries, DiaryAdherenceSummary Summary);

public sealed record ListMyFoodDiaryQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<FoodDiaryEntryResponse>>;

internal sealed class DiaryPeriodValidator : AbstractValidator<ListMyFoodDiaryQuery>
{
    public DiaryPeriodValidator() =>
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).Must((q, to) => to.DayNumber - q.From.DayNumber <= 92).WithMessage("The range cannot exceed 92 days.");
}

internal sealed class ListMyFoodDiaryHandler(IUserContext user, IReadDbContext db) : IQueryHandler<ListMyFoodDiaryQuery, IReadOnlyList<FoodDiaryEntryResponse>>
{
    public async Task<Result<IReadOnlyList<FoodDiaryEntryResponse>>> Handle(ListMyFoodDiaryQuery query, CancellationToken cancellationToken) =>
        Result.Success(await FoodDiaryMapping.ListAsync(
            db, db.FoodDiaryEntries.Where(e => e.ClientId == user.UserId && e.LocalDate >= query.From && e.LocalDate <= query.To), cancellationToken));
}

/// <summary>
/// The dietitian reads diary entries the client shared with them in the active tenant (RF-30). Trainers have no access
/// to diary entries at all (acceptance test 6). Audited.
/// </summary>
public sealed record ListClientFoodDiaryQuery(Guid ClientId, DateOnly From, DateOnly To) : IQuery<DietitianFoodDiaryResponse>;

internal sealed class ListClientFoodDiaryValidator : AbstractValidator<ListClientFoodDiaryQuery>
{
    public ListClientFoodDiaryValidator() =>
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).Must((q, to) => to.DayNumber - q.From.DayNumber <= 92).WithMessage("The range cannot exceed 92 days.");
}

internal sealed class ListClientFoodDiaryHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListClientFoodDiaryQuery, DietitianFoodDiaryResponse>
{
    public async Task<Result<DietitianFoodDiaryResponse>> Handle(ListClientFoodDiaryQuery query, CancellationToken cancellationToken)
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

        var shared = db.FoodDiaryEntries.Where(e => e.ClientId == query.ClientId && e.TenantId == dietitian.Value.TenantId
                                                    && e.DietitianProfileId == dietitian.Value.Profile.Id
                                                    && e.LocalDate >= query.From && e.LocalDate <= query.To);
        var items = await FoodDiaryMapping.ListAsync(db, shared, cancellationToken);
        await audit.RecordReadAsync("FoodDiaryEntry.List", relationship.Value.Id, query.ClientId, dietitian.Value.TenantId, cancellationToken);
        return new DietitianFoodDiaryResponse(items, FoodDiaryMapping.Summarize(items, query.From, query.To));
    }
}

/// <summary>The dietitian the entry was shared with comments on it; the client is notified (RF-30).</summary>
public sealed record CommentOnFoodDiaryEntryCommand(Guid EntryId, string Message) : ICommand;

internal sealed class CommentOnFoodDiaryEntryValidator : AbstractValidator<CommentOnFoodDiaryEntryCommand>
{
    public CommentOnFoodDiaryEntryValidator() => RuleFor(c => c.Message).NotEmpty().MaximumLength(2000);
}

internal sealed class CommentOnFoodDiaryEntryHandler(
    ICareAccess access, IFoodDiaryRepository entries, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CommentOnFoodDiaryEntryCommand>
{
    public async Task<Result> Handle(CommentOnFoodDiaryEntryCommand command, CancellationToken cancellationToken)
    {
        var dietitian = await access.GetCurrentProfessionalAsync(Discipline.Dietitian, requireVerified: true, cancellationToken);
        if (dietitian.IsFailure)
        {
            return dietitian.Error;
        }

        var entry = await entries.GetByIdAsync(command.EntryId, cancellationToken);
        if (entry is null || entry.DietitianProfileId != dietitian.Value.Profile.Id || entry.TenantId != dietitian.Value.TenantId)
        {
            if (entry is not null)
            {
                await audit.RecordDeniedAsync(nameof(FoodDiaryEntry), entry.Id, dietitian.Value.TenantId, cancellationToken);
            }

            return FoodDiaryErrors.NotFound;
        }

        var relationship = await access.GetActiveRelationshipAsync(dietitian.Value, entry.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var result = entry.Comment(dietitian.Value.Profile, command.Message, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Comment, nameof(FoodDiaryEntry), entry.Id, entry.ClientId, entry.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

public sealed record SharedMealPlanItem(Guid PlanId, string Name, string DietitianName, int VersionNumber, DateOnly StartDate, DateOnly? EndDate, DateTimeOffset PublishedAt);

public sealed record NutritionSummaryResponse(Guid ClientId, IReadOnlyList<SharedMealPlanItem> ActivePlans, DiaryAdherenceSummary Last7Days);

/// <summary>
/// What a trainer (or practitioner) may know about nutrition for workout coordination (§6 "summary if authorized"):
/// which meal plans are active and how the last week's diary related to them, in counts. No foods, no assessments, no
/// diary text. Requires a <c>NutritionSummary</c> grant, covers the relationship's organization only, and is audited.
/// </summary>
public sealed record GetSharedNutritionSummaryQuery(Guid RelationshipId) : IQuery<NutritionSummaryResponse>;

internal sealed class GetSharedNutritionSummaryHandler(ICareAccess access, IReadDbContext db, IAuditLog audit, TimeProvider clock)
    : IQueryHandler<GetSharedNutritionSummaryQuery, NutritionSummaryResponse>
{
    public async Task<Result<NutritionSummaryResponse>> Handle(GetSharedNutritionSummaryQuery query, CancellationToken cancellationToken)
    {
        var relationship = await access.GetSharedAccessAsync(query.RelationshipId, DataCategory.NutritionSummary, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var r = relationship.Value;
        var client = await db.UserProfiles.FirstAsync(u => u.Id == r.ClientId, cancellationToken);
        var to = TimeZones.LocalDate(clock.GetUtcNow(), client.TimeZoneId);
        var from = to.AddDays(-6);

        var plans = await (
                from plan in db.MealPlans
                join version in db.MealPlanVersions on plan.CurrentVersionId equals version.Id
                join dietitian in db.ProfessionalProfiles on plan.DietitianProfileId equals dietitian.Id
                where plan.ClientId == r.ClientId && plan.TenantId == r.TenantId && plan.Status == MealPlanStatus.Published
                select new SharedMealPlanItem(
                    plan.Id, version.Details.Name, dietitian.DisplayName, version.VersionNumber, version.Details.StartDate, version.Details.EndDate, version.PublishedAt))
            .ToListAsync(cancellationToken);

        var entries = await db.FoodDiaryEntries
            .Where(e => e.ClientId == r.ClientId && e.TenantId == r.TenantId && e.LocalDate >= from && e.LocalDate <= to)
            .Select(e => new { e.LocalDate, e.Adherence, e.WaterMl })
            .ToListAsync(cancellationToken);

        await audit.RecordReadAsync("NutritionSummary", r.Id, r.ClientId, r.TenantId, cancellationToken);
        return new NutritionSummaryResponse(
            r.ClientId!.Value, plans, FoodDiaryMapping.Summarize(entries.Select(e => (e.LocalDate, e.Adherence, e.WaterMl)), from, to));
    }
}

internal static class FoodDiaryMapping
{
    public static async Task<IReadOnlyList<FoodDiaryEntryResponse>> ListAsync(
        IReadDbContext db, IQueryable<FoodDiaryEntry> entries, CancellationToken cancellationToken)
    {
        var rows = await entries.Include(e => e.Comments).OrderByDescending(e => e.EatenAt).Take(1000).ToListAsync(cancellationToken);

        var tenantIds = rows.Where(e => e.TenantId != null).Select(e => e.TenantId!.Value).Distinct().ToList();
        var authorIds = rows.SelectMany(e => e.Comments.Select(c => c.AuthorProfileId)).Distinct().ToList();
        var tenants = await db.Tenants.Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var authors = await db.ProfessionalProfiles.Where(p => authorIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);

        return rows.Select(e => new FoodDiaryEntryResponse(
                e.Id, e.TenantId, e.TenantId is { } t ? tenants.GetValueOrDefault(t) : null, e.MealPlanId, e.MealPlanVersionNumber, e.PlannedMealId, e.MealName,
                e.EatenAt, e.LocalDate, e.Description, e.WaterMl, e.Adherence, e.ClientNotes,
                e.Comments.OrderBy(c => c.CreatedAt)
                    .Select(c => new FoodDiaryCommentResponse(c.Id, authors.GetValueOrDefault(c.AuthorProfileId, string.Empty), c.Message, c.CreatedAt))
                    .ToList(),
                e.RecordedAt, e.UpdatedAt))
            .ToList();
    }

    public static DiaryAdherenceSummary Summarize(IReadOnlyList<FoodDiaryEntryResponse> entries, DateOnly from, DateOnly to) =>
        Summarize(entries.Select(e => (e.LocalDate, e.Adherence, e.WaterMl)), from, to);

    public static DiaryAdherenceSummary Summarize(IEnumerable<(DateOnly Date, MealAdherence? Adherence, int? WaterMl)> entries, DateOnly from, DateOnly to)
    {
        var list = entries.ToList();
        return new DiaryAdherenceSummary(
            from, to, list.Count, list.Select(e => e.Date).Distinct().Count(),
            list.Count(e => e.Adherence == MealAdherence.AsPlanned),
            list.Count(e => e.Adherence == MealAdherence.Partly),
            list.Count(e => e.Adherence == MealAdherence.Different),
            list.Count(e => e.Adherence == MealAdherence.Unplanned),
            list.Count(e => e.Adherence is null && e.WaterMl is null),
            list.Sum(e => e.WaterMl ?? 0));
    }
}
