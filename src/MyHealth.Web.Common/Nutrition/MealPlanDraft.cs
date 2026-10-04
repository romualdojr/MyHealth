using System.Text.Json;
using MyHealth.Web.Common.Api;

namespace MyHealth.Web.Common.Nutrition;

// Mutable editing state for the dietitian's screens. The API's meal plan content is immutable; the editor copies it into
// these classes, lets the dietitian change them, and sends them back as the bodies of PUT /meal-plans/{id} and
// PUT /meal-plans/{id}/meals. Existing ids are sent back so diary entries stay linked to the same meal across versions;
// new items go without an id and the API assigns one (the editor reloads the plan after every save).

/// <summary>The body of <c>PUT /meal-plans/{id}</c> and of the details in <c>POST /meal-plans</c>.</summary>
public sealed record MealPlanDetailsBody(string Name, string? Goal, DateOnly StartDate, DateOnly? EndDate, string? Notes);

public sealed record MealItemBody(Guid? Id, string Food, decimal? Quantity, string? Unit, string? HouseholdMeasure, string? Notes, IReadOnlyList<string> Substitutions);

public sealed record PlannedMealBody(Guid? Id, string Name, TimeOnly? Time, string? Notes, IReadOnlyList<MealItemBody> Items);

public sealed class MealPlanDetailsForm
{
    public string Name { get; set; } = string.Empty;

    public string? Goal { get; set; }

    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public DateOnly? EndDate { get; set; }

    public string? Notes { get; set; }

    public static MealPlanDetailsForm From(MealPlanDetails details) => new()
    {
        Name = details.Name,
        Goal = details.Goal,
        StartDate = details.StartDate,
        EndDate = details.EndDate,
        Notes = details.Notes,
    };

    public MealPlanDetailsBody ToBody() => new(Name.Trim(), Text.Blank(Goal), StartDate, EndDate, Text.Blank(Notes));
}

public sealed class MealItemDraft
{
    public Guid? Id { get; init; }

    public string Food { get; set; } = string.Empty;

    public decimal? Quantity { get; set; }

    public string? Unit { get; set; } = "g";

    public string? HouseholdMeasure { get; set; }

    public string? Notes { get; set; }

    /// <summary>Substitutions as the dietitian types them: one per line.</summary>
    public string? Substitutions { get; set; }

    /// <summary>A food line left completely empty; it is dropped on save instead of blocking it.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Food) && Quantity is null && string.IsNullOrWhiteSpace(HouseholdMeasure) && string.IsNullOrWhiteSpace(Notes) && string.IsNullOrWhiteSpace(Substitutions);

    public bool HasPortion => (Quantity is > 0 && !string.IsNullOrWhiteSpace(Unit)) || !string.IsNullOrWhiteSpace(HouseholdMeasure);

    public MealItemBody ToBody() =>
        new(Id, Food.Trim(), Quantity, Quantity is null ? null : Text.Blank(Unit), Text.Blank(HouseholdMeasure), Text.Blank(Notes),
            [.. (Substitutions ?? string.Empty).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0)]);
}

public sealed class MealDraft
{
    public Guid? Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public TimeOnly? Time { get; set; }

    public string? Notes { get; set; }

    public List<MealItemDraft> Items { get; init; } = [];

    public PlannedMealBody ToBody() => new(Id, Name.Trim(), Time, Text.Blank(Notes), [.. Items.Where(i => !i.IsBlank).Select(i => i.ToBody())]);
}

/// <summary>The meals of a meal plan's draft as the dietitian edits them.</summary>
public sealed class MealPlanDraft
{
    /// <summary>The usual day, offered when a plan is empty: name and time of each meal.</summary>
    public static readonly IReadOnlyList<(string Name, TimeOnly Time)> UsualDay =
    [
        ("Café da manhã", new TimeOnly(7, 0)),
        ("Lanche da manhã", new TimeOnly(10, 0)),
        ("Almoço", new TimeOnly(12, 30)),
        ("Lanche da tarde", new TimeOnly(16, 0)),
        ("Jantar", new TimeOnly(19, 30)),
    ];

    public List<MealDraft> Meals { get; init; } = [];

    public static MealPlanDraft From(MealPlanContent content) => new()
    {
        Meals =
        [
            .. content.Meals.OrderBy(m => m.Order).Select(m => new MealDraft
            {
                Id = m.Id,
                Name = m.Name,
                Time = m.Time,
                Notes = m.Notes,
                Items =
                [
                    .. m.Items.Select(i => new MealItemDraft
                    {
                        Id = i.Id,
                        Food = i.Food,
                        Quantity = i.Quantity,
                        Unit = i.Unit,
                        HouseholdMeasure = i.HouseholdMeasure,
                        Notes = i.Notes,
                        Substitutions = i.Substitutions.Count == 0 ? null : string.Join('\n', i.Substitutions),
                    }),
                ],
            }),
        ],
    };

    /// <summary>Adds the next meal of the usual day that is not in the plan yet (or a blank one) and returns it.</summary>
    public MealDraft AddMeal()
    {
        var (name, time) = UsualDay.FirstOrDefault(u => Meals.All(m => !string.Equals(m.Name.Trim(), u.Name, StringComparison.OrdinalIgnoreCase)));
        var meal = new MealDraft { Name = name ?? $"Refeição {Meals.Count + 1}", Time = name is null ? null : time };
        meal.Items.Add(new MealItemDraft());
        Meals.Add(meal);
        return meal;
    }

    /// <summary>Fills an empty plan with the usual day's meals, each with one empty food line.</summary>
    public void AddUsualDay()
    {
        foreach (var (name, time) in UsualDay)
        {
            Meals.Add(new MealDraft { Name = name, Time = time, Items = [new MealItemDraft()] });
        }
    }

    /// <summary>The API orders meals by time; the editor shows them the same way after each save.</summary>
    public IReadOnlyList<PlannedMealBody> ToBody() => [.. Meals.Select(m => m.ToBody())];

    /// <summary>A stable text form of what would be sent, to tell whether there are unsaved changes.</summary>
    public string Snapshot() => JsonSerializer.Serialize(ToBody(), MyHealthApiClient.Json);

    /// <summary>
    /// What blocks saving or publishing, in the dietitian's words. The API applies the same rules (and is the authority):
    /// every food needs a portion (quantity with unit, or a household measure), and a published plan needs meals with foods.
    /// </summary>
    public IReadOnlyList<string> SavingIssues()
    {
        var issues = new List<string>();
        foreach (var meal in Meals)
        {
            var name = string.IsNullOrWhiteSpace(meal.Name) ? "Refeição sem nome" : meal.Name.Trim();
            if (string.IsNullOrWhiteSpace(meal.Name))
            {
                issues.Add("Dê um nome a cada refeição.");
            }

            foreach (var item in meal.Items.Where(i => !i.IsBlank))
            {
                if (string.IsNullOrWhiteSpace(item.Food))
                {
                    issues.Add($"{name}: preencha ou remova o alimento sem nome.");
                }
                else if (!item.HasPortion)
                {
                    issues.Add($"{name} · {item.Food.Trim()}: informe a quantidade com unidade ou uma medida caseira.");
                }
            }
        }

        return [.. issues.Distinct()];
    }

    public IReadOnlyList<string> PublishingIssues()
    {
        var issues = new List<string>();
        if (Meals.Count == 0)
        {
            issues.Add("Adicione pelo menos uma refeição.");
        }

        issues.AddRange(SavingIssues());
        issues.AddRange(Meals.Where(m => m.Items.All(i => i.IsBlank)).Select(m => $"{(string.IsNullOrWhiteSpace(m.Name) ? "Refeição sem nome" : m.Name.Trim())}: adicione alimentos."));
        return [.. issues.Distinct()];
    }
}

/// <summary>A nutrition assessment as the dietitian fills it in (RF-28); also used to correct one.</summary>
public sealed class NutritionAssessmentForm
{
    public DateOnly AssessedOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public string? Goals { get; set; }

    public string? ReportedRestrictions { get; set; }

    public string? ReportedAllergies { get; set; }

    public string? Preferences { get; set; }

    public string? MealTimes { get; set; }

    /// <summary>Values of <see cref="NutritionLabels.CommonMeasurements"/>, by metric; empty ones are not sent.</summary>
    public Dictionary<string, decimal?> Measurements { get; init; } = NutritionLabels.CommonMeasurements.ToDictionary(m => m.Metric, _ => (decimal?)null);

    /// <summary>Measurements recorded with another method or metric (e.g. by the phone app); kept as they are.</summary>
    public List<MeasurementItem> OtherMeasurements { get; init; } = [];

    public string? FollowUp { get; set; }

    public DateOnly? NextFollowUpOn { get; set; }

    public string? Notes { get; set; }

    public static NutritionAssessmentForm From(NutritionAssessmentContent content)
    {
        var form = new NutritionAssessmentForm
        {
            AssessedOn = content.AssessedOn,
            Goals = content.Goals,
            ReportedRestrictions = content.ReportedRestrictions,
            ReportedAllergies = content.ReportedAllergies,
            Preferences = content.Preferences,
            MealTimes = content.MealTimes,
            FollowUp = content.FollowUp,
            NextFollowUpOn = content.NextFollowUpOn,
            Notes = content.Notes,
        };

        foreach (var m in content.Measurements)
        {
            var known = NutritionLabels.CommonMeasurements.Any(c => c.Metric == m.Metric && c.Method == m.Method && c.Unit == m.Unit);
            if (known)
            {
                form.Measurements[m.Metric] = m.Value;
            }
            else
            {
                form.OtherMeasurements.Add(m);
            }
        }

        return form;
    }

    /// <summary>The <c>assessment</c> body of <c>POST /nutrition-assessments</c> and of a correction.</summary>
    public object ToBody() => new
    {
        assessedOn = AssessedOn,
        goals = Text.Blank(Goals),
        reportedRestrictions = Text.Blank(ReportedRestrictions),
        reportedAllergies = Text.Blank(ReportedAllergies),
        preferences = Text.Blank(Preferences),
        mealTimes = Text.Blank(MealTimes),
        measurements = NutritionLabels.CommonMeasurements
            .Where(c => Measurements.GetValueOrDefault(c.Metric) is not null)
            .Select(c => new MeasurementItem(c.Metric, c.Method, c.Unit, Measurements[c.Metric]!.Value))
            .Concat(OtherMeasurements)
            .ToList(),
        followUp = Text.Blank(FollowUp),
        nextFollowUpOn = NextFollowUpOn,
        notes = Text.Blank(Notes),
    };
}

internal static class Text
{
    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
