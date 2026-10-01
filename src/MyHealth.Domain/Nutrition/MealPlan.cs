using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Nutrition;

public enum MealPlanStatus
{
    Draft = 1,
    Published = 2,
    Ended = 3,
}

/// <summary>
/// A dietary prescription for one client (RF-29). Mirrors the training plan: the dietitian edits <see cref="Draft"/>,
/// and publishing freezes it into an immutable, numbered <see cref="MealPlanVersion"/> that notifies the client. Only a
/// verified dietitian can create or publish one.
/// </summary>
public sealed class MealPlan : AggregateRoot<Guid>
{
    private MealPlan(Guid id) : base(id) { }

    private MealPlan() { }

    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid DietitianProfileId { get; private set; }

    public Guid DietitianUserId { get; private set; }

    public MealPlanDetails Details { get; private set; } = null!;

    public MealPlanContent Draft { get; private set; } = MealPlanContent.Empty;

    public MealPlanStatus Status { get; private set; }

    public int CurrentVersionNumber { get; private set; }

    public Guid? CurrentVersionId { get; private set; }

    public bool HasUnpublishedChanges { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<MealPlan> Create(Guid tenantId, ProfessionalProfile dietitian, Guid clientId, MealPlanDetails details, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A meal plan always belongs to a tenant.", nameof(tenantId));
        }

        var check = NutritionRules.EnsureDietitian(dietitian);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var valid = ValidateDetails(details);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        return new MealPlan(Guid.CreateVersion7(now))
        {
            TenantId = tenantId,
            ClientId = clientId,
            DietitianProfileId = dietitian.Id,
            DietitianUserId = dietitian.UserId,
            Details = details,
            Status = MealPlanStatus.Draft,
            HasUnpublishedChanges = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsOwnedBy(Guid dietitianProfileId) => DietitianProfileId == dietitianProfileId;

    public Result UpdateDetails(MealPlanDetails details, DateTimeOffset now)
    {
        if (Status == MealPlanStatus.Ended)
        {
            return MealPlanErrors.Ended;
        }

        var valid = ValidateDetails(details);
        if (valid.IsFailure)
        {
            return valid;
        }

        Details = details;
        Touch(now);
        return Result.Success();
    }

    public Result UpdateDraft(MealPlanContent content, DateTimeOffset now)
    {
        if (Status == MealPlanStatus.Ended)
        {
            return MealPlanErrors.Ended;
        }

        var valid = content.ValidateStructure();
        if (valid.IsFailure)
        {
            return valid;
        }

        Draft = content;
        Touch(now);
        return Result.Success();
    }

    public Result<MealPlanVersion> Publish(ProfessionalProfile dietitian, string changeDescription, DateTimeOffset now)
    {
        if (!IsOwnedBy(dietitian.Id))
        {
            return MealPlanErrors.NotOwner;
        }

        var check = NutritionRules.EnsureDietitian(dietitian);
        if (check.IsFailure)
        {
            return check.Error;
        }

        if (Status == MealPlanStatus.Ended)
        {
            return MealPlanErrors.Ended;
        }

        if (!HasUnpublishedChanges)
        {
            return MealPlanErrors.NothingToPublish;
        }

        var valid = Draft.ValidateForPublishing();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var version = MealPlanVersion.Create(this, CurrentVersionNumber + 1, changeDescription, now);
        CurrentVersionNumber = version.VersionNumber;
        CurrentVersionId = version.Id;
        Status = MealPlanStatus.Published;
        HasUnpublishedChanges = false;
        UpdatedAt = now;
        Raise(new MealPlanPublished(Id, TenantId, version.Id, version.VersionNumber, ClientId, now));
        return version;
    }

    public Result End(DateTimeOffset now)
    {
        if (Status == MealPlanStatus.Ended)
        {
            return MealPlanErrors.Ended;
        }

        Status = MealPlanStatus.Ended;
        UpdatedAt = now;
        return Result.Success();
    }

    private void Touch(DateTimeOffset now)
    {
        HasUnpublishedChanges = true;
        UpdatedAt = now;
    }

    private static Result ValidateDetails(MealPlanDetails details) =>
        details.EndDate < details.StartDate ? MealPlanErrors.InvalidDates : Result.Success();
}

/// <summary>An immutable published snapshot of a meal plan, with its author (RF-29: auditable previous versions).</summary>
public sealed class MealPlanVersion : AggregateRoot<Guid>
{
    private MealPlanVersion(Guid id) : base(id) { }

    private MealPlanVersion() { }

    public Guid MealPlanId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid DietitianProfileId { get; private set; }

    public Guid DietitianUserId { get; private set; }

    public int VersionNumber { get; private set; }

    public MealPlanDetails Details { get; private set; } = null!;

    public MealPlanContent Content { get; private set; } = MealPlanContent.Empty;

    public string ChangeDescription { get; private set; } = null!;

    public DateTimeOffset PublishedAt { get; private set; }

    internal static MealPlanVersion Create(MealPlan plan, int versionNumber, string changeDescription, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            MealPlanId = plan.Id,
            TenantId = plan.TenantId,
            ClientId = plan.ClientId,
            DietitianProfileId = plan.DietitianProfileId,
            DietitianUserId = plan.DietitianUserId,
            VersionNumber = versionNumber,
            Details = plan.Details,
            Content = plan.Draft,
            ChangeDescription = changeDescription.Trim(),
            PublishedAt = now,
        };
}

public sealed record MealPlanPublished(Guid PlanId, Guid TenantId, Guid VersionId, int VersionNumber, Guid ClientId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public interface IMealPlanRepository : IRepository<MealPlan, Guid>;

public interface IMealPlanVersionRepository : IRepository<MealPlanVersion, Guid>
{
    Task<MealPlanVersion?> GetByNumberAsync(Guid mealPlanId, int versionNumber, CancellationToken cancellationToken = default);
}

public static class MealPlanErrors
{
    public static readonly Error NotFound = Error.NotFound("MealPlan.NotFound", "The meal plan was not found.");
    public static readonly Error VersionNotFound = Error.NotFound("MealPlan.VersionNotFound", "The meal plan version was not found.");
    public static readonly Error NotOwner = Error.Forbidden("MealPlan.NotOwner", "Only the dietitian who owns this plan can change it.");
    public static readonly Error Ended = Error.Conflict("MealPlan.Ended", "The meal plan has ended and can no longer be changed.");
    public static readonly Error NothingToPublish = Error.Conflict("MealPlan.NothingToPublish", "There are no changes to publish.");
    public static readonly Error NoMeals = Error.Validation("MealPlan.NoMeals", "A meal plan needs at least one meal to be published.");
    public static readonly Error InvalidDates = Error.Validation("MealPlan.InvalidDates", "The end date must be on or after the start date.");

    public static Error DuplicateIds(string item) => Error.Validation("MealPlan.DuplicateIds", $"Each {item} must have a unique id.");

    public static Error ItemWithoutPortion(string food) =>
        Error.Validation("MealPlan.ItemWithoutPortion", $"'{food}' needs a quantity with a unit or a household measure.");

    public static Error EmptyMeal(string meal) => Error.Validation("MealPlan.EmptyMeal", $"Meal '{meal}' has no foods.");
}

public static class NutritionErrors
{
    public static readonly Error FollowUpBeforeAssessment = Error.Validation(
        "Nutrition.FollowUpBeforeAssessment", "The next follow-up cannot be before the assessment date.");
}
