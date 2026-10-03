using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Domain.Assistant;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Therapies;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Assistant;

/// <summary>
/// What the assistant may know to answer one message: only the areas the client's consent allows, only recent records,
/// and ids it needs to prepare suggestions. Nothing from other people is included.
/// </summary>
public sealed record AssistantContext(
    string ClientFirstName,
    DateOnly Today,
    string LocalTime,
    IReadOnlyList<AssistantScope> Scopes,
    TrainingContext? Training,
    NutritionContext? Nutrition,
    TherapyContext? Therapies);

public sealed record TrainingContext(
    IReadOnlyList<ContextPlan> Plans,
    ContextWorkout? CurrentWorkout,
    IReadOnlyList<ContextRecentWorkout> RecentWorkouts,
    IReadOnlyList<ContextExerciseHistory> ExerciseHistory);

public sealed record ContextPlan(Guid PlanId, string Name, string Trainer, string Organization, IReadOnlyList<string> Sessions);

public sealed record ContextWorkout(
    Guid WorkoutSessionId, string SessionName, string Trainer, IReadOnlyList<ContextPlannedExercise> Exercises, Guid? CurrentExerciseId, int? CurrentSetNumber);

public sealed record ContextPlannedExercise(Guid ExerciseId, Guid PlannedExerciseId, string Name, string Target, int SetsLogged);

public sealed record ContextRecentWorkout(Guid WorkoutSessionId, DateOnly Date, string SessionName, string Trainer, string Status, string? ReportedDiscomfort);

public sealed record ContextExerciseHistory(Guid ExerciseId, string Name, DateOnly Date, IReadOnlyList<string> Sets);

public sealed record NutritionContext(IReadOnlyList<ContextMeal> TodaysMeals);

public sealed record ContextMeal(
    Guid MealPlanId, Guid PlannedMealId, string Name, string? Time, string Dietitian, string Organization, bool LoggedToday, IReadOnlyList<string> Items);

public sealed record TherapyContext(IReadOnlyList<ContextTherapySession> Sessions);

public sealed record ContextTherapySession(Guid TherapySessionId, string Modality, string Practitioner, string Organization, string When, string Status);

/// <summary>Where the client is in the app when they talk (the workout screen sends its session and current set).</summary>
public sealed record AssistantScreenContext(Guid? WorkoutSessionId, Guid? ExerciseId, int? SetNumber);

internal sealed class AssistantContextBuilder(IReadDbContext db, TimeProvider clock)
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public async Task<AssistantContext> BuildAsync(UserProfile profile, IReadOnlyList<AssistantScope> scopes, AssistantScreenContext screen, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);

        return new AssistantContext(
            profile.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? profile.FullName,
            today,
            local.ToString("HH:mm", CultureInfo.InvariantCulture),
            scopes,
            scopes.Contains(AssistantScope.Training) ? await TrainingAsync(profile.Id, screen, now, zone, cancellationToken) : null,
            scopes.Contains(AssistantScope.Nutrition) ? await NutritionAsync(profile.Id, today, cancellationToken) : null,
            scopes.Contains(AssistantScope.Therapies) ? await TherapiesAsync(profile.Id, now, zone, cancellationToken) : null);
    }

    private async Task<TrainingContext> TrainingAsync(Guid clientId, AssistantScreenContext screen, DateTimeOffset now, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var plans = await (
                from plan in db.TrainingPlans
                join version in db.TrainingPlanVersions on plan.CurrentVersionId equals version.Id
                join trainer in db.ProfessionalProfiles on plan.TrainerProfileId equals trainer.Id
                join tenant in db.Tenants on plan.TenantId equals tenant.Id
                where plan.ClientId == clientId && plan.Status == PlanStatus.Published
                select new { plan.Id, version.Details.Name, version.Content, Trainer = trainer.DisplayName, Organization = tenant.Name })
            .ToListAsync(cancellationToken);

        var trainers = await (
                from r in db.CareRelationships
                join p in db.ProfessionalProfiles on r.ProfessionalProfileId equals p.Id
                where r.ClientId == clientId
                select new { p.Id, p.DisplayName })
            .Distinct()
            .ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);

        var since = now.AddDays(-14);
        var recent = await db.WorkoutSessions
            .Include(s => s.Sets)
            .Where(s => s.ClientId == clientId && s.StartedAt >= since)
            .OrderByDescending(s => s.StartedAt)
            .Take(6)
            .ToListAsync(cancellationToken);

        ContextWorkout? current = null;
        if (screen.WorkoutSessionId is { } sessionId && recent.FirstOrDefault(s => s.Id == sessionId && s.IsOpen) is { } open)
        {
            var planned = plans.SelectMany(p => p.Content.Sessions).FirstOrDefault(s => s.Id == open.PlannedSessionId);
            var exercises = (planned?.Exercises ?? []).OrderBy(e => e.Order).Select(e => new ContextPlannedExercise(
                e.ExerciseId, e.Id, e.ExerciseName, Target(e), open.Sets.Count(s => s.PlannedExerciseId == e.Id || s.ExerciseId == e.ExerciseId))).ToList();
            current = new ContextWorkout(
                open.Id, open.PlannedSessionName, trainers.GetValueOrDefault(open.TrainerProfileId, "seu treinador"), exercises, screen.ExerciseId, screen.SetNumber);
        }

        var names = plans.SelectMany(p => p.Content.Sessions).SelectMany(s => s.Exercises).GroupBy(e => e.ExerciseId).ToDictionary(g => g.Key, g => g.First().ExerciseName);
        var history = recent
            .Where(s => s.Status == WorkoutStatus.Completed)
            .SelectMany(s => s.Sets.GroupBy(set => set.ExerciseId).Select(g => new ContextExerciseHistory(
                g.Key,
                names.GetValueOrDefault(g.Key, "exercício"),
                s.LocalDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.StartedAt, zone).DateTime),
                g.OrderBy(set => set.SetNumber).Select(Describe).ToList())))
            .GroupBy(h => h.ExerciseId)
            .Select(g => g.First())
            .ToList();

        return new TrainingContext(
            plans.Select(p => new ContextPlan(p.Id, p.Name, p.Trainer, p.Organization, p.Content.Sessions.OrderBy(s => s.Order).Select(s => s.Name).ToList())).ToList(),
            current,
            recent.Select(s => new ContextRecentWorkout(
                s.Id,
                s.LocalDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.StartedAt, zone).DateTime),
                s.PlannedSessionName,
                trainers.GetValueOrDefault(s.TrainerProfileId, "seu treinador"),
                s.Status.ToString(),
                s.LatestReportedDiscomfort() is { } d ? d.Details ?? d.Subject ?? "sim" : null)).ToList(),
            history);
    }

    private async Task<NutritionContext> NutritionAsync(Guid clientId, DateOnly today, CancellationToken cancellationToken)
    {
        var plans = await (
                from plan in db.MealPlans
                join version in db.MealPlanVersions on plan.CurrentVersionId equals version.Id
                join dietitian in db.ProfessionalProfiles on plan.DietitianProfileId equals dietitian.Id
                join tenant in db.Tenants on plan.TenantId equals tenant.Id
                where plan.ClientId == clientId && plan.Status == MealPlanStatus.Published
                select new { plan.Id, version.Content, Dietitian = dietitian.DisplayName, Organization = tenant.Name })
            .ToListAsync(cancellationToken);

        var logged = await db.FoodDiaryEntries
            .Where(e => e.ClientId == clientId && e.LocalDate == today && e.PlannedMealId != null)
            .Select(e => e.PlannedMealId!.Value)
            .ToListAsync(cancellationToken);

        return new NutritionContext(plans
            .SelectMany(p => p.Content.Meals.OrderBy(m => m.Order).Select(m => new ContextMeal(
                p.Id, m.Id, m.Name, m.Time?.ToString("HH:mm", CultureInfo.InvariantCulture), p.Dietitian, p.Organization, logged.Contains(m.Id),
                m.Items.Select(Portion).ToList())))
            .ToList());
    }

    private async Task<TherapyContext> TherapiesAsync(Guid clientId, DateTimeOffset now, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var windowStart = now.AddDays(-14);
        var windowEnd = now.AddDays(30);
        var sessions = await (
                from s in db.TherapySessions
                join m in db.TherapyModalities on s.ModalityId equals m.Id
                join p in db.ProfessionalProfiles on s.PractitionerProfileId equals p.Id
                join t in db.Tenants on s.TenantId equals t.Id
                where s.ClientId == clientId && s.StartsAt >= windowStart && s.StartsAt <= windowEnd && s.Status != TherapySessionStatus.Cancelled
                orderby s.StartsAt
                select new { s.Id, s.StartsAt, s.Status, Modality = m.Name, Practitioner = p.DisplayName, Organization = t.Name })
            .Take(10)
            .ToListAsync(cancellationToken);

        return new TherapyContext(sessions.Select(s => new ContextTherapySession(
            s.Id, s.Modality, s.Practitioner, s.Organization, TimeZoneInfo.ConvertTime(s.StartsAt, zone).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture),
            s.Status.ToString())).ToList());
    }

    private static string Portion(MealItem item)
    {
        var portion = item.HouseholdMeasure ?? (item.Quantity is { } quantity ? $"{quantity.ToString("0.##", Brazil)} {item.Unit}" : null);
        return portion is null ? item.Food : $"{item.Food} ({portion})";
    }

    private static string Target(PlannedExercise exercise)
    {
        var sets = exercise.Sets;
        if (sets.Count == 0)
        {
            return string.Empty;
        }

        var first = sets[0];
        var reps = first.MinRepetitions is { } min ? first.MaxRepetitions is { } max && max != min ? $"{min}–{max}" : $"{min}" : null;
        var load = first.SuggestedLoadKg is { } kg ? $" com {kg.ToString("0.##", Brazil)} kg" : string.Empty;
        return reps is null ? $"{sets.Count} séries" : $"{sets.Count} séries de {reps} repetições{load}";
    }

    private static string Describe(SetLog set) =>
        set.Repetitions is { } reps
            ? set.LoadKg is { } kg ? $"{reps} × {kg.ToString("0.##", Brazil)} kg" : $"{reps} repetições"
            : set.DurationSeconds is { } seconds ? $"{seconds} s" : "série registrada";
}
