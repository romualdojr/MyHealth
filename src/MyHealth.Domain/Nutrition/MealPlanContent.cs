using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Nutrition;

/// <summary>
/// One food in a meal. The portion is a quantity with a unit (e.g. 120 g), a household measure (e.g. "2 colheres de
/// sopa"), or both. Substitutions are what the client may eat instead.
/// </summary>
public sealed record MealItem(
    Guid Id,
    string Food,
    decimal? Quantity,
    string? Unit,
    string? HouseholdMeasure,
    string? Notes,
    IReadOnlyList<string> Substitutions);

/// <summary>A meal of the plan. Its id is stable across versions so diary entries can point to it.</summary>
public sealed record PlannedMeal(Guid Id, string Name, TimeOnly? Time, int Order, string? Notes, IReadOnlyList<MealItem> Items);

public sealed record MealPlanDetails(string Name, string? Goal, DateOnly StartDate, DateOnly? EndDate, string? Notes);

/// <summary>The meals of a plan. Immutable: drafts are replaced, published content is never changed.</summary>
public sealed record MealPlanContent(IReadOnlyList<PlannedMeal> Meals)
{
    public static readonly MealPlanContent Empty = new([]);

    public PlannedMeal? FindMeal(Guid mealId) => Meals.FirstOrDefault(m => m.Id == mealId);

    public Result ValidateStructure()
    {
        if (Meals.Select(m => m.Id).Distinct().Count() != Meals.Count)
        {
            return MealPlanErrors.DuplicateIds("meal");
        }

        var items = Meals.SelectMany(m => m.Items).ToList();
        if (items.Select(i => i.Id).Distinct().Count() != items.Count)
        {
            return MealPlanErrors.DuplicateIds("meal item");
        }

        foreach (var item in items)
        {
            var hasQuantity = item.Quantity is > 0 && !string.IsNullOrWhiteSpace(item.Unit);
            if (!hasQuantity && string.IsNullOrWhiteSpace(item.HouseholdMeasure))
            {
                return MealPlanErrors.ItemWithoutPortion(item.Food);
            }
        }

        return Result.Success();
    }

    public Result ValidateForPublishing()
    {
        var structure = ValidateStructure();
        if (structure.IsFailure)
        {
            return structure;
        }

        if (Meals.Count == 0)
        {
            return MealPlanErrors.NoMeals;
        }

        var empty = Meals.FirstOrDefault(m => m.Items.Count == 0);
        return empty is null ? Result.Success() : MealPlanErrors.EmptyMeal(empty.Name);
    }
}
