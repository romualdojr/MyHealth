using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.Coaching;

// What the AI coach receives about one client. Pseudonymised: no name, e-mail, birth date or other identifier; ids are
// only those of the plan and the exercise library, so suggestions can point at them. Health history, nutrition and
// therapy records are never included; wellbeing only when the client also shares it with this trainer.

public sealed record CoachSetTarget(int? MinRepetitions, int? MaxRepetitions, int? DurationSeconds, decimal? SuggestedLoadKg);

public sealed record CoachPlannedExercise(
    Guid PlannedExerciseId, Guid ExerciseId, string Name, IReadOnlyList<string> MuscleGroups, int RestSeconds, string? Instructions,
    IReadOnlyList<CoachSetTarget> Sets);

public sealed record CoachPlannedSession(Guid PlannedSessionId, string Name, SessionKind Kind, IReadOnlyList<CoachPlannedExercise> Exercises, AerobicTarget? Aerobic);

public sealed record CoachPlan(
    string Name, string Goal, TrainingLevel Level, int SuggestedSessionsPerWeek, int WeeksSinceStart, int PublishedVersion, bool DraftDiffersFromPublished,
    IReadOnlyList<CoachPlannedSession> Sessions);

public sealed record CoachLoggedSet(int SetNumber, int? Repetitions, decimal? LoadKg, int? DurationSeconds, int? Difficulty);

/// <summary>What the client did in one exercise in one workout, newest first in <see cref="CoachExerciseHistory.Workouts"/>.</summary>
public sealed record CoachExerciseWorkout(DateOnly Date, int? PerceivedExertion, IReadOnlyList<CoachLoggedSet> Sets);

public sealed record CoachExerciseHistory(Guid PlannedExerciseId, string Name, IReadOnlyList<CoachExerciseWorkout> Workouts);

public sealed record CoachWorkout(
    DateOnly Date, string SessionName, WorkoutStatus Status, int? PerceivedExertion, bool ReportedDiscomfort, string? DiscomfortDetails, string? Comments);

public sealed record CoachBodyCheck(DateOnly Date, string Subject, string Answer, string? Details);

public sealed record CoachAdherence(int CompletedLast28Days, int ExpectedLast28Days, IReadOnlyList<int> CompletedPerWeek);

public sealed record CoachWellbeing(int Days, double? AverageSleep, double? AverageEnergy);

/// <summary>Exercises the coach may propose as a swap: same muscle groups, available to this organization, not in the plan.</summary>
public sealed record CoachAlternative(Guid ExerciseId, string Name, IReadOnlyList<string> MuscleGroups, IReadOnlyList<string> Equipment, string ForPlannedExerciseName);

public sealed record CoachContext(
    DateOnly Today,
    CoachPlan Plan,
    CoachAdherence Adherence,
    IReadOnlyList<CoachWorkout> RecentWorkouts,
    IReadOnlyList<CoachExerciseHistory> ExerciseHistory,
    IReadOnlyList<CoachBodyCheck> BodyChecks,
    CoachWellbeing? Wellbeing,
    IReadOnlyList<CoachAlternative> Alternatives)
{
    /// <summary>Discomfort or a worse/new body check in the last two weeks: no progression is proposed until the trainer talks to the client.</summary>
    public bool HasRecentDiscomfort =>
        RecentWorkouts.Any(w => w.ReportedDiscomfort && w.Date >= Today.AddDays(-14))
        || BodyChecks.Any(b => b.Answer is "Worse" or "New" && b.Date >= Today.AddDays(-14));

    public CoachPlannedExercise? FindPlannedExercise(Guid plannedExerciseId) =>
        Plan.Sessions.SelectMany(s => s.Exercises).FirstOrDefault(e => e.PlannedExerciseId == plannedExerciseId);

    public CoachPlannedSession? FindSessionOf(Guid plannedExerciseId) =>
        Plan.Sessions.FirstOrDefault(s => s.Exercises.Any(e => e.PlannedExerciseId == plannedExerciseId));
}

/// <summary>Builds the coach's view of one client's training from the trainer's own records.</summary>
public sealed class CoachContextBuilder(IReadDbContext db, TimeProvider clock)
{
    public const int WindowDays = 42;
    private const int MaxWorkouts = 20;
    private const int WorkoutsPerExercise = 4;
    private const int AlternativesPerExercise = 3;
    private const int MaxTextLength = 300;

    public async Task<CoachContext> BuildAsync(TrainingPlan plan, bool includeWellbeing, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var since = now.AddDays(-WindowDays);

        var planned = plan.Draft.Sessions.SelectMany(s => s.Exercises).ToList();
        var exerciseIds = planned.Select(e => e.ExerciseId).Distinct().ToList();
        var library = await db.Exercises
            .Where(e => exerciseIds.Contains(e.Id))
            .Select(e => new { e.Id, e.MuscleGroups })
            .ToDictionaryAsync(e => e.Id, e => (IReadOnlyList<string>)e.MuscleGroups, cancellationToken);

        var sessions = plan.Draft.Sessions.OrderBy(s => s.Order).Select(s => new CoachPlannedSession(
            s.Id,
            s.Name,
            s.Kind,
            [
                .. s.Exercises.OrderBy(e => e.Order).Select(e => new CoachPlannedExercise(
                    e.Id, e.ExerciseId, e.ExerciseName, library.GetValueOrDefault(e.ExerciseId, []), e.RestSeconds, e.Instructions,
                    [.. e.Sets.OrderBy(t => t.Order).Select(t => new CoachSetTarget(t.MinRepetitions, t.MaxRepetitions, t.DurationSeconds, t.SuggestedLoadKg))])),
            ],
            s.Aerobic)).ToList();

        var weeksSinceStart = Math.Max(0, (today.DayNumber - plan.Details.StartDate.DayNumber) / 7);
        var coachPlan = new CoachPlan(
            plan.Details.Name, plan.Details.Goal, plan.Details.Level, plan.Details.SuggestedSessionsPerWeek, weeksSinceStart,
            plan.CurrentVersionNumber, plan.HasUnpublishedChanges, sessions);

        // Workouts of this plan only: the coach looks at how the client responds to this prescription.
        var workouts = await db.WorkoutSessions
            .Include(w => w.Sets)
            .Where(w => w.TrainingPlanId == plan.Id && w.TenantId == plan.TenantId && w.StartedAt >= since)
            .OrderByDescending(w => w.StartedAt)
            .Take(MaxWorkouts)
            .ToListAsync(cancellationToken);

        DateOnly DateOf(WorkoutSession w) => w.LocalDate ?? DateOnly.FromDateTime((w.CompletedAt ?? w.StartedAt).UtcDateTime);

        var recent = workouts.Select(w => new CoachWorkout(
            DateOf(w), w.PlannedSessionName, w.Status, w.Feedback?.PerceivedExertion, w.Feedback?.ReportedDiscomfort == true,
            Trim(w.Feedback?.DiscomfortDetails), Trim(w.Feedback?.Comments))).ToList();

        var history = planned
            .Select(p => new CoachExerciseHistory(
                p.Id,
                p.ExerciseName,
                [
                    .. workouts
                        .Where(w => w.Status == WorkoutStatus.Completed)
                        .Select(w => new
                        {
                            Workout = w,
                            Sets = w.Sets.Where(s => s.PlannedExerciseId == p.Id || (s.PlannedExerciseId == null && s.ExerciseId == p.ExerciseId)).OrderBy(s => s.SetNumber).ToList(),
                        })
                        .Where(x => x.Sets.Count > 0)
                        .Take(WorkoutsPerExercise)
                        .Select(x => new CoachExerciseWorkout(
                            DateOf(x.Workout), x.Workout.Feedback?.PerceivedExertion,
                            [.. x.Sets.Select(s => new CoachLoggedSet(s.SetNumber, s.Repetitions, s.LoadKg, s.DurationSeconds, s.Difficulty))])),
                ]))
            .Where(h => h.Workouts.Count > 0)
            .ToList();

        var bodyChecks = workouts
            .SelectMany(w => w.BodyChecks.Where(b => b.ReportedToTrainer))
            .OrderByDescending(b => b.At)
            .Select(b => new CoachBodyCheck(DateOnly.FromDateTime(b.At.UtcDateTime), b.Subject, b.Answer.ToString(), Trim(b.Details)))
            .ToList();

        var completedDates = workouts.Where(w => w.Status == WorkoutStatus.Completed).Select(DateOf).ToList();
        var perWeek = Enumerable.Range(0, 4)
            .Select(week => completedDates.Count(d => d > today.AddDays(-7 * (week + 1)) && d <= today.AddDays(-7 * week)))
            .ToList();
        var adherence = new CoachAdherence(perWeek.Sum(), ExpectedSessions(plan.Details, today), perWeek);

        CoachWellbeing? wellbeing = null;
        if (includeWellbeing)
        {
            var from = today.AddDays(-14);
            var checkIns = await db.WellbeingCheckIns
                .Where(c => c.ClientId == plan.ClientId && c.LocalDate >= from)
                .Select(c => new { c.SleepQuality, c.Energy })
                .ToListAsync(cancellationToken);
            wellbeing = new CoachWellbeing(
                checkIns.Count, Average(checkIns.Select(c => c.SleepQuality)), Average(checkIns.Select(c => c.Energy)));
        }

        var alternatives = await AlternativesAsync(plan, sessions, cancellationToken);

        return new CoachContext(today, coachPlan, adherence, recent, history, bodyChecks, wellbeing, alternatives);
    }

    private async Task<List<CoachAlternative>> AlternativesAsync(TrainingPlan plan, List<CoachPlannedSession> sessions, CancellationToken cancellationToken)
    {
        var inPlan = sessions.SelectMany(s => s.Exercises).Select(e => e.ExerciseId).ToHashSet();
        var muscles = sessions.SelectMany(s => s.Exercises).SelectMany(e => e.MuscleGroups).Distinct().ToList();
        if (muscles.Count == 0)
        {
            return [];
        }

        var candidates = await db.Exercises
            .Where(e => !e.IsArchived && (e.TenantId == null || e.TenantId == plan.TenantId) && e.MuscleGroups.Any(m => muscles.Contains(m)))
            .OrderBy(e => e.Name)
            .Take(200)
            .Select(e => new { e.Id, e.Name, e.MuscleGroups, e.Equipment })
            .ToListAsync(cancellationToken);

        var result = new List<CoachAlternative>();
        foreach (var exercise in sessions.SelectMany(s => s.Exercises).DistinctBy(e => e.ExerciseId))
        {
            result.AddRange(candidates
                .Where(c => !inPlan.Contains(c.Id) && c.MuscleGroups.Intersect(exercise.MuscleGroups).Any())
                .OrderByDescending(c => c.MuscleGroups.Intersect(exercise.MuscleGroups).Count())
                .Take(AlternativesPerExercise)
                .Select(c => new CoachAlternative(c.Id, c.Name, c.MuscleGroups, c.Equipment, exercise.Name)));
        }

        return result;
    }

    /// <summary>
    /// Sessions the plan asked for in the last 28 days: the weekly frequency over the days the plan has been running in that
    /// window, so a plan that started last week is not counted as four weeks of missed workouts.
    /// </summary>
    public static int ExpectedSessions(PlanDetails details, DateOnly today)
    {
        var days = Math.Clamp(today.DayNumber - details.StartDate.DayNumber + 1, 0, 28);
        return (int)Math.Round(details.SuggestedSessionsPerWeek * days / 7.0, MidpointRounding.AwayFromZero);
    }

    private static double? Average(IEnumerable<int?> values)
    {
        var answered = values.OfType<int>().ToList();
        return answered.Count == 0 ? null : Math.Round(answered.Average(), 1);
    }

    private static string? Trim(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= MaxTextLength ? text.Trim() : text[..MaxTextLength].Trim() + "…";
}
