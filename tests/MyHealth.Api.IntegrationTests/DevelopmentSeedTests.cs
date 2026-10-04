using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Coaching;
using MyHealth.Domain.Coaching;
using MyHealth.Infrastructure.Persistence;

namespace MyHealth.Api.IntegrationTests;

/// <summary>The Development seed runs end to end through the real commands and leaves every trainer screen with data.</summary>
public sealed class DevelopmentSeedTests(ApiFactory factory) : IntegrationTest(factory)
{
    private const string Password = "MyHealth-dev-2026";

    private sealed record LoginResponse(string AccessToken);

    [Fact]
    public async Task The_development_seed_creates_working_accounts_plans_and_workouts()
    {
        Scenario.Factory.EnsureAvailable();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string Email(string name) => $"{name}-{suffix}@myhealth.dev";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:Development:Password"] = Password,
            ["Seed:Development:OperatorEmail"] = Email("operador"),
            ["Seed:Development:TrainerEmail"] = Email("treinador"),
            ["Seed:Development:DietitianEmail"] = Email("nutri"),
            ["Seed:Development:ClientAnaEmail"] = Email("ana"),
            ["Seed:Development:ClientBrunoEmail"] = Email("bruno"),
            ["Seed:Development:ClientCarlaEmail"] = Email("carla"),
            ["Seed:Development:ClientDanielaEmail"] = Email("daniela"),
            ["Seed:Development:ClientEduardoEmail"] = Email("eduardo"),
            ["Seed:Development:ClientFernandaEmail"] = Email("fernanda"),
            ["Seed:Development:ClientGabrielEmail"] = Email("gabriel"),
            ["Seed:Development:TherapistEmail"] = Email("terapeuta"),
            ["Seed:Development:TherapyModalityCode"] = $"acupuntura_{new string(suffix.Select(c => (char)('a' + c % 26)).ToArray())}",
        }).Build();
        var seeder = new DevelopmentSeeder(Scenario.Factory.Services, configuration, TimeProvider.System, NullLogger.Instance);

        await seeder.SeedAsync(Ct);
        await seeder.SeedAsync(Ct); // Runs once.

        // The trainer signs in with the seeded password and belongs to the seeded organization.
        var http = Scenario.Factory.CreateClient();
        var login = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = Email("treinador"), password = Password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        http.DefaultRequestHeaders.Authorization = new("Bearer", (await Scenario.Read<LoginResponse>(login)).AccessToken);

        var me = await Scenario.Read<MyHealth.Application.Users.MeResponse>(await http.GetAsync("/api/v1/me", Ct));
        var tenant = me.Memberships.ShouldHaveSingleItem();
        tenant.TenantName.ShouldBe("Estúdio Movimento");

        // Plans, workouts and relationships the trainer screens list.
        await using var scope = Scenario.Factory.Services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plans = db.TrainingPlans.Where(p => p.TenantId == tenant.TenantId).ToList();
        plans.Count.ShouldBe(2);
        plans.ShouldAllBe(p => p.CurrentVersionNumber == 1);
        var anaPlan = plans.Single(p => p.Details.Name == "Força — fase 1");
        db.WorkoutSessions.Count(w => w.TrainingPlanId == anaPlan.Id && w.Status == MyHealth.Domain.Training.Workouts.WorkoutStatus.Completed).ShouldBe(5);
        db.WorkoutSessions.Single(w => w.TenantId == tenant.TenantId && w.Feedback!.ReportedDiscomfort).Reply.ShouldBeNull();
        db.CareRelationships.Count(r => r.TenantId == tenant.TenantId).ShouldBe(12);

        // Ana shares the heart rate of her last two workouts (measured, with a gap and one value she typed in).
        var heartRates = db.WorkoutHeartRates.Where(h => h.TenantId == tenant.TenantId).ToList();
        heartRates.Count.ShouldBe(2);
        heartRates.ShouldAllBe(h => h.Summary != null && h.Summary.SampleCount > 40 && h.Summary.SelfReportedSampleCount == 1);

        // The dietitian's screens: Ana's assessment, published meal plan and diary (one entry commented), and the other
        // patients: Daniela's week of diary, Eduardo's draft plan, Fernanda's ended and current plans.
        var mealPlans = db.MealPlans.Where(p => p.TenantId == tenant.TenantId).ToList();
        mealPlans.Select(p => (p.Details.Name, p.Status, p.CurrentVersionNumber)).OrderBy(p => p.Name).ShouldBe(
        [
            ("Controle glicêmico", MyHealth.Domain.Nutrition.MealPlanStatus.Draft, 0),
            ("Fase 1 — adaptação", MyHealth.Domain.Nutrition.MealPlanStatus.Ended, 1),
            ("Fase 2 — déficit leve", MyHealth.Domain.Nutrition.MealPlanStatus.Published, 1),
            ("Reeducação alimentar", MyHealth.Domain.Nutrition.MealPlanStatus.Published, 1),
            ("Vegetariano — hipertrofia", MyHealth.Domain.Nutrition.MealPlanStatus.Published, 1),
        ]);
        db.NutritionAssessments.Count(a => a.TenantId == tenant.TenantId).ShouldBe(5);
        var diary = db.FoodDiaryEntries.Where(e => e.TenantId == tenant.TenantId).ToList();
        diary.Count.ShouldBe(8 + 29 + 5);
        diary.Count(e => e.Comments.Count > 0).ShouldBe(3);

        // The assistants: three of the dietitian's patients allow AI analysis; the therapist follows Ana (care plan, two documented
        // sessions and one scheduled), Carla and Bruno.
        db.CareRelationships.Where(r => r.TenantId == tenant.TenantId).AsEnumerable()
            .Count(r => r.HasActiveGrant(MyHealth.Domain.CareRelationships.DataCategory.AiCoaching)).ShouldBe(6);
        db.TherapyCarePlans.Count(p => p.TenantId == tenant.TenantId).ShouldBe(1);
        db.TherapySessions.Where(s => s.TenantId == tenant.TenantId).Select(s => s.Status).AsEnumerable().Order()
            .ShouldBe([MyHealth.Domain.Therapies.TherapySessionStatus.Scheduled, MyHealth.Domain.Therapies.TherapySessionStatus.Completed, MyHealth.Domain.Therapies.TherapySessionStatus.Completed]);

        // Ana allowed AI analysis and is at the top of the range in the squat: the coach proposes a progression.
        var coach = scope.ServiceProvider.GetRequiredService<CoachContextBuilder>();
        var context = await coach.BuildAsync(anaPlan, includeWellbeing: true, Ct);
        context.Wellbeing.ShouldNotBeNull().Days.ShouldBe(5);
        context.ExerciseHistory.ShouldContain(h => h.Name == "Agachamento livre" && h.Workouts.Count == 3);
        var model = scope.ServiceProvider.GetRequiredService<MyHealth.Application.Abstractions.Coaching.ITrainingCoachModel>();
        var reply = await model.AnalyzeAsync(new(CoachTools.SystemPrompt, context, "{}", null, CoachTools.All), Ct);
        var outcome = CoachSuggestionPolicy.Apply(reply!.ToolCalls, context, DateTimeOffset.UtcNow);
        outcome.Suggestions.ShouldContain(s => s.Kind == CoachSuggestionKind.ChangeLoad && s.ExerciseName == "Agachamento livre" && s.LoadKg == 42m);
    }
}
