using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Common;
using MyHealth.Application.Nutrition;
using MyHealth.Application.TrainerAssistant;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.ProfessionalAssistant;

public sealed record NutritionAssessmentSummary(
    DateOnly AssessedOn,
    string? Goals,
    string? Restrictions,
    string? Allergies,
    string? Preferences,
    string? MealTimes,
    IReadOnlyList<Measurement> Measurements,
    string? FollowUp,
    DateOnly? NextFollowUpOn,
    string? Notes);

public sealed record MealSummary(string Name, TimeOnly? Time, string? Notes, IReadOnlyList<string> Items);

public sealed record MealPlanSummary(string Name, string? Goal, MealPlanStatus Status, int Version, DateOnly StartDate, DateOnly? EndDate, IReadOnlyList<MealSummary> Meals);

/// <summary>The last 14 days of the diary shared with the dietitian, in counts.</summary>
public sealed record DiaryCounts(int Meals, int AsPlanned, int Partly, int Different, int Unplanned, int DaysWithEntries, int? AverageWaterMlPerDay);

public sealed record DiaryNote(DateOnly Date, string? Meal, MealAdherence? Adherence, string? Description, string? ClientNotes);

public sealed record NutritionPatientContext(
    NutritionAssessmentSummary? LatestAssessment, MealPlanSummary? Plan, DiaryCounts Last14Days, IReadOnlyList<DiaryNote> RecentEntries);

/// <summary>The dietitian's patients: latest assessment, current meal plan and the last two weeks of diary; proposals are meal plans.</summary>
internal sealed class NutritionAssistantWorkspace(IReadDbContext db, TimeProvider clock) : AssistantWorkspace
{
    public const string DraftMealPlan = ProfessionalAssistantTools.DraftMealPlan;
    private const int DiaryDays = 14;
    private const int RecentEntries = 12;
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public override Discipline Discipline => Discipline.Dietitian;

    public override string AuditResourceType => "DietitianAssistant";

    public override string SystemPrompt => """
        You are the MyHealth assistant for a registered dietitian (nutricionista, CRN) in Brazil. Answer in Brazilian Portuguese, concisely,
        for a nutrition professional. You see the dietitian's patients who allow AI analysis, in the JSON context: each with an alias
        ("Paciente 1", "Paciente 2"…) and, under "nutrition", the latest assessment (goals, restrictions, allergies, preferences, meal times,
        measurements, next follow-up), the current meal plan and the last 14 days of food diary (adherence counts and recent entries).
        "focusAlias" is the patient the dietitian is looking at, if any. "patientsWithoutConsent" patients exist but are not shared with you:
        if asked about someone not in the context, say they have not allowed AI analysis in the app.

        What you do:
        - Summaries of one patient or of all: adherence, unplanned meals, patterns in the diary, follow-ups due, and what needs attention first.
        - Help create meal plans: when asked for a plan, a menu or a new version, call draft_meal_plan with complete meals: names, times that
          fit the patient's routine, foods with a quantity and unit or a household measure, and practical substitutions. Use the assessment
          (goals, preferences, restrictions) and what the diary shows (meals often skipped or replaced). The dietitian reviews the proposal,
          creates a draft from it and publishes it; nothing reaches the patient before that.
        - Answer questions about the plans and the diary.

        Rules you must never break:
        - Always refer to patients by their alias exactly as written ("Paciente 2"); never invent or ask for names.
        - Never include a food the patient is allergic to or must avoid, not even as a substitution.
        - No medical diagnosis and no medication or supplement doses; clinical conditions in the record are the dietitian's to interpret:
          point out what they should confirm.
        - Use only data in the context. If there is no assessment, say so, keep the proposal simple and list what to confirm.
        - At most 3 draft_meal_plan calls per answer. Do not say a plan was created: the dietitian creates it from the proposal.
        """;

    public override IReadOnlyList<AssistantToolDefinition> Tools { get; } =
    [
        Define(DraftMealPlan, "Propose a complete meal plan for one patient. The dietitian reviews it and creates a draft from it.", """
            {
              "type": "object",
              "properties": {
                "patient": { "type": "string", "description": "The patient's alias, e.g. \"Paciente 1\"." },
                "name": { "type": "string", "description": "Plan name, e.g. \"Reeducação alimentar\"." },
                "goal": { "type": "string" },
                "start_date": { "type": "string", "description": "yyyy-MM-dd; today when omitted." },
                "notes": { "type": "string", "description": "General guidance for the patient (water, preparation)." },
                "meals": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string" },
                      "time": { "type": "string", "description": "HH:mm" },
                      "notes": { "type": "string" },
                      "items": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "properties": {
                            "food": { "type": "string" },
                            "quantity": { "type": "number", "exclusiveMinimum": 0 },
                            "unit": { "type": "string", "description": "g, ml, unidade…" },
                            "household_measure": { "type": "string", "description": "e.g. \"2 colheres de sopa\"" },
                            "notes": { "type": "string" },
                            "substitutions": { "type": "array", "items": { "type": "string" } }
                          },
                          "required": ["food"]
                        }
                      }
                    },
                    "required": ["name", "items"]
                  }
                },
                "reason": { "type": "string", "description": "Why this plan, citing the assessment and the diary." }
              },
              "required": ["patient", "name", "meals", "reason"]
            }
            """),
    ];

    public override async Task<WorkspaceData> LoadAsync(ActingProfessional professional, CancellationToken cancellationToken)
    {
        var tenantId = professional.TenantId;
        var profileId = professional.Profile.Id;
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var relationships = await db.CareRelationships
            .Where(r => r.TenantId == tenantId && r.ProfessionalProfileId == profileId && r.Status == RelationshipStatus.Active && r.ClientId != null)
            .ToListAsync(cancellationToken);
        var clientIds = relationships.Select(r => r.ClientId!.Value).ToList();
        if (clientIds.Count == 0)
        {
            return new WorkspaceData([]);
        }

        var names = await db.UserProfiles.Where(u => clientIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        // Content is stored as JSON, so the latest assessment and the current plan are picked in memory.
        var assessments = (await db.NutritionAssessments
                .Where(a => a.TenantId == tenantId && a.DietitianProfileId == profileId && clientIds.Contains(a.ClientId))
                .ToListAsync(cancellationToken))
            .GroupBy(a => a.ClientId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.AssessedOn).ThenBy(a => a.CreatedAt).Last());
        var plans = await db.MealPlans
            .Where(p => p.TenantId == tenantId && p.DietitianProfileId == profileId && clientIds.Contains(p.ClientId) && p.Status != MealPlanStatus.Ended)
            .ToListAsync(cancellationToken);
        var currentPlans = plans
            .GroupBy(p => p.ClientId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Status == MealPlanStatus.Published).ThenByDescending(p => p.UpdatedAt).First());
        var mealNames = plans.SelectMany(p => p.Draft.Meals).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First().Name);

        var since = now.AddDays(-DiaryDays);
        var diary = (await db.FoodDiaryEntries
                .Where(e => e.TenantId == tenantId && e.DietitianProfileId == profileId && clientIds.Contains(e.ClientId) && e.EatenAt >= since)
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.ClientId);

        var patients = relationships
            .Select(r =>
            {
                var clientId = r.ClientId!.Value;
                var assessment = assessments.GetValueOrDefault(clientId);
                var plan = currentPlans.GetValueOrDefault(clientId);
                var entries = diary[clientId].ToList();
                var context = Context(assessment, plan, entries, mealNames);

                var detail = $"{context.Last14Days.DaysWithEntries} de {DiaryDays} dias com diário";
                if (assessment?.Content.NextFollowUpOn is { } followUp)
                {
                    detail += $" · retorno {followUp.ToString("dd/MM", Brazil)}";
                }

                string? attention = null;
                if (assessment?.Content.NextFollowUpOn < today)
                {
                    attention = "retorno atrasado";
                }
                else if (plan is null)
                {
                    attention = "sem plano";
                }
                else if (context.Last14Days.Unplanned + context.Last14Days.Different > context.Last14Days.Meals / 3 && context.Last14Days.Meals > 0)
                {
                    attention = "baixa adesão";
                }

                var summary = new PatientSummary(
                    clientId, names.GetValueOrDefault(clientId, "Paciente"), r.Id, r.HasActiveGrant(DataCategory.AiCoaching), plan?.Id, plan?.Details.Name, detail, attention);
                return new PatientEntry(summary, Nutrition: context);
            })
            .OrderBy(p => p.Summary.Name, StringComparer.CurrentCulture)
            .ToList();
        return new WorkspaceData(patients);
    }

    private static NutritionPatientContext Context(
        NutritionAssessment? assessment, MealPlan? plan, List<FoodDiaryEntry> entries, Dictionary<Guid, string> mealNames)
    {
        NutritionAssessmentSummary? latest = null;
        if (assessment is { Content: var c })
        {
            latest = new NutritionAssessmentSummary(
                c.AssessedOn, c.Goals, c.ReportedRestrictions, c.ReportedAllergies, c.Preferences, c.MealTimes, c.Measurements, c.FollowUp, c.NextFollowUpOn, c.Notes);
        }

        MealPlanSummary? current = null;
        if (plan is not null)
        {
            current = new MealPlanSummary(
                plan.Details.Name, plan.Details.Goal, plan.Status, plan.CurrentVersionNumber, plan.Details.StartDate, plan.Details.EndDate,
                [.. plan.Draft.Meals.OrderBy(m => m.Order).Select(m => new MealSummary(m.Name, m.Time, m.Notes, [.. m.Items.Select(Describe)]))]);
        }

        var meals = entries.Where(e => e.WaterMl is null || e.PlannedMealId is not null || !string.IsNullOrWhiteSpace(e.Description)).ToList();
        var water = entries.Where(e => e.WaterMl is not null).GroupBy(e => e.LocalDate).Select(g => g.Sum(e => e.WaterMl!.Value)).ToList();
        var counts = new DiaryCounts(
            meals.Count,
            meals.Count(e => e.Adherence == MealAdherence.AsPlanned),
            meals.Count(e => e.Adherence == MealAdherence.Partly),
            meals.Count(e => e.Adherence == MealAdherence.Different),
            meals.Count(e => e.Adherence == MealAdherence.Unplanned),
            entries.Select(e => e.LocalDate).Distinct().Count(),
            water.Count > 0 ? (int)Math.Round(water.Average()) : null);

        var recent = meals
            .OrderByDescending(e => e.EatenAt)
            .Take(RecentEntries)
            .Select(e => new DiaryNote(
                e.LocalDate, e.MealName ?? (e.PlannedMealId is { } id ? mealNames.GetValueOrDefault(id) : null), e.Adherence, e.Description, e.ClientNotes))
            .ToList();
        return new NutritionPatientContext(latest, current, counts, recent);
    }

    /// <summary>"Arroz integral — 100 g (4 colheres de sopa); trocas: Quinoa (120 g)".</summary>
    private static string Describe(MealItem item)
    {
        var portion = string.Join(" ", new[]
        {
            item.Quantity is { } q ? $"{q.ToString("0.##", CultureInfo.InvariantCulture)} {item.Unit}".Trim() : null,
            item.HouseholdMeasure is { } h ? (item.Quantity is null ? h : $"({h})") : null,
        }.Where(p => p is not null));
        var text = portion.Length > 0 ? $"{item.Food} — {portion}" : item.Food;
        if (item.Notes is { } notes)
        {
            text += $"; {notes}";
        }

        return item.Substitutions.Count > 0 ? $"{text}; trocas: {string.Join(", ", item.Substitutions)}" : text;
    }

    public override AssistantProposals Proposals(
        IReadOnlyList<AssistantToolCall> calls, IReadOnlyList<IncludedPatient> included, ProfessionalAssistantContext context, WorkspaceData data,
        StudentAliases aliases)
    {
        var proposals = new List<MealPlanProposal>();
        foreach (var call in calls.Where(c => c.Name == DraftMealPlan))
        {
            if (proposals.Count == MaxProposals || Read<DraftMealPlanInput>(call) is not { } input || PatientOf(input.Patient, included, context) is not { } patient)
            {
                continue;
            }

            var meals = new List<PlannedMealInput>();
            foreach (var meal in input.Meals ?? [])
            {
                // An item needs a portion (quantity or household measure), and a meal needs at least one item, like the plan editor.
                var items = (meal.Items ?? [])
                    .Where(i => !string.IsNullOrWhiteSpace(i.Food) && (i.Quantity > 0 || !string.IsNullOrWhiteSpace(i.HouseholdMeasure)))
                    .Take(50)
                    .Select(i => new MealItemInput(
                        null, Clean(i.Food, 200)!, i.Quantity > 0 ? Math.Min(i.Quantity.Value, 100_000) : null, i.Quantity > 0 ? Clean(i.Unit, 20) : null,
                        Clean(i.HouseholdMeasure, 200), Clean(aliases.Reveal(i.Notes ?? string.Empty), 1000),
                        [.. (i.Substitutions ?? []).Select(s => Clean(s, 200)).OfType<string>().Take(20)]))
                    .ToList();
                if (string.IsNullOrWhiteSpace(meal.Name) || items.Count == 0)
                {
                    continue;
                }

                TimeOnly? time = TimeOnly.TryParseExact(meal.Time?.Trim() ?? string.Empty, ["HH:mm", "H:mm", "HH'h'mm", "HH'h'"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                    ? t
                    : null;
                meals.Add(new PlannedMealInput(null, Clean(meal.Name, 200)!, time, Clean(aliases.Reveal(meal.Notes ?? string.Empty), 2000), items));
            }

            if (meals.Count == 0)
            {
                continue;
            }

            var startDate = DateOnly.TryParseExact(input.StartDate ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                            && start >= context.Today
                ? start
                : context.Today;
            var details = new MealPlanDetailsInput(
                Clean(input.Name, 200) ?? "Plano alimentar", Clean(aliases.Reveal(input.Goal ?? string.Empty), 500), startDate, null,
                Clean(aliases.Reveal(input.Notes ?? string.Empty), 4000));

            // The same rules as creating the plan and saving its meals.
            if (!new MealPlanDetailsInputValidator().Validate(details).IsValid || meals.Count > 20
                || meals.Any(m => !new PlannedMealInputValidator().Validate(m).IsValid))
            {
                continue;
            }

            var summary = patient.Entry.Summary;
            proposals.Add(new MealPlanProposal(summary.ClientId, summary.Name, details, [.. meals.Take(20)], Clean(aliases.Reveal(input.Reason ?? string.Empty), 2000)));
        }

        return new AssistantProposals(proposals, []);
    }

    private sealed record DraftMealPlanInput(string? Patient, string? Name, string? Goal, string? StartDate, string? Notes, List<MealInput>? Meals, string? Reason);

    private sealed record MealInput(string? Name, string? Time, string? Notes, List<ItemInput>? Items);

    private sealed record ItemInput(string? Food, decimal? Quantity, string? Unit, string? HouseholdMeasure, string? Notes, List<string>? Substitutions);
}
