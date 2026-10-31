using System.Globalization;
using MyHealth.Web.Common.Api;

namespace MyHealth.Web.Components.Shared;

/// <summary>
/// Which screen of the client app the preview shows: the training path (<c>treino</c>), a session's exercises
/// (<c>sessao</c>), one exercise of the workout screen (<c>ativo</c>) or today's meals (<c>nutri</c>).
/// </summary>
public sealed record PhoneScreen(string Kind, Guid? SessionId = null, int Exercise = 0)
{
    public static readonly PhoneScreen Path = new("treino");
    public static readonly PhoneScreen Meals = new("nutri");

    public bool IsTraining => Kind is "treino" or "sessao" or "ativo";
}

public enum TrailNodeState
{
    Done,
    Current,
    Locked,

    /// <summary>A reward node that is already unlocked (the week's chest after every workout of the week).</summary>
    Open,
}

/// <summary>One row of the training path: a week divider, a dotted gap or a round node.</summary>
public sealed record TrailItem(string Type, string Label, string Color = "", TrailNodeState State = TrailNodeState.Locked, string? Icon = null,
    string When = "", int X = 0, string? Bubble = null, Guid? SessionId = null, string? Toast = null);

/// <summary>
/// The client's published training plan and workouts, as the client app's training screens show them. Only real data:
/// the path is the plan's sessions in order, a week at a time from the plan's start; the next session is the one after
/// the last workout done (the app has no calendar), and the streak counts the weeks in a row with every suggested workout.
/// </summary>
public sealed class TrainingPreview
{
    private static readonly int[] Offsets = [0, -45, -70, -45, 0, 45, 70, 45];

    public required PlanVersionDetail Version { get; init; }

    /// <summary>The version before this one, to mark the exercises that are new in this version.</summary>
    public PlanVersionDetail? Previous { get; init; }

    public required string TrainerName { get; init; }

    public required string OrganizationName { get; init; }

    /// <summary>The client's workouts of this plan, any version.</summary>
    public required IReadOnlyList<WorkoutSessionItem> Workouts { get; init; }

    public WorkoutSessionDetail? LastWorkout { get; init; }

    public required IReadOnlyDictionary<Guid, ExerciseItem> Exercises { get; init; }

    public required DateOnly Today { get; init; }

    public PlanDetails Details => Version.Details;

    public IReadOnlyList<PlannedSession> Sessions => [.. Version.Content.Sessions.OrderBy(s => s.Order)];

    public int PerWeek => Math.Max(1, Details.SuggestedSessionsPerWeek);

    public bool StartsLater => Today < Details.StartDate;

    public int? TotalWeeks => Details.EndDate is { } end ? Math.Max(1, (end.DayNumber - Details.StartDate.DayNumber) / 7 + 1) : null;

    /// <summary>The plan's week today, from 1; capped to the last week once the plan is over.</summary>
    public int CurrentWeek => Math.Min(Math.Max(1, WeekOf(Today)), TotalWeeks ?? int.MaxValue);

    public IReadOnlyList<WorkoutSessionItem> Completed =>
        [.. Workouts.Where(w => w.Status == "Completed").OrderBy(w => w.CompletedAt ?? w.StartedAt)];

    /// <summary>"sex": the weekday the plan's next week starts (weeks count from the plan's start date).</summary>
    public string RenewsOn =>
        ClientAppText.Brazil.DateTimeFormat.GetAbbreviatedDayName(Details.StartDate.AddDays(7 * CurrentWeek).DayOfWeek).TrimEnd('.');

    public WorkoutSessionItem? LastCompleted => Completed is [.., var last] ? last : null;

    public int DoneThisWeek => DoneInWeek(CurrentWeek);

    public int Streak
    {
        get
        {
            var weeks = 0;
            for (var week = CurrentWeek - 1; week >= 1 && DoneInWeek(week) >= PerWeek; week--)
            {
                weeks++;
            }

            return weeks + (DoneThisWeek >= PerWeek ? 1 : 0);
        }
    }

    public string FirstName => ClientAppText.FirstName(TrainerName);

    public IReadOnlyList<TrailItem> Trail()
    {
        var items = new List<TrailItem>();
        var sessions = Sessions;
        var week = CurrentWeek;
        var offset = 0;
        int NextX() => Offsets[offset++ % Offsets.Length];

        if (week > 1)
        {
            var before = DoneInWeek(week - 1);
            items.Add(new TrailItem("week", $"Semana {week - 1} · {before} de {PerWeek}{(before >= PerWeek ? " ✓" : string.Empty)}",
                before >= PerWeek ? "var(--color-accent-700)" : "var(--color-neutral-700)"));
        }

        items.Add(new TrailItem("week", StartsLater ? $"Semana 1 · começa {Details.StartDate:dd/MM}" : $"Semana {week} · esta semana", "var(--color-neutral-700)"));

        var thisWeek = Completed.Where(w => WeekOf(DateOf(w)) == week).ToList();
        foreach (var done in thisWeek)
        {
            var label = ClientAppText.Short(done.PlannedSessionName);
            var when = $"{label} · {ClientAppText.Day(DateOf(done))}";
            items.Add(new TrailItem("node", label, State: TrailNodeState.Done, When: when, X: NextX(), Toast: $"Feito · {when}"));
        }

        var cursor = NextSessionIndex(sessions);
        if (sessions.Count > 0)
        {
            var remaining = Math.Max(0, PerWeek - thisWeek.Count);
            for (var i = 0; i < remaining; i++)
            {
                var planned = sessions[(cursor + i) % sessions.Count];
                var label = ClientAppText.Short(planned.Name);
                items.Add(i == 0
                    ? new TrailItem("node", label, State: TrailNodeState.Current, When: $"{label} · próximo treino", X: NextX(), Bubble: "COMEÇAR", SessionId: planned.Id)
                    : new TrailItem("node", label, State: TrailNodeState.Locked, X: NextX(), SessionId: planned.Id, Toast: "Bloqueado até chegar a vez dele"));
            }

            cursor += remaining;
        }

        var complete = thisWeek.Count >= PerWeek;
        items.Add(new TrailItem("node", string.Empty, State: complete ? TrailNodeState.Open : TrailNodeState.Locked, Icon: "gift", When: "Semana completa",
            X: NextX(), Toast: complete ? "Semana completa!" : $"Complete os {PerWeek} treinos da semana para abrir"));

        if (TotalWeeks is not { } total || week + 1 <= total)
        {
            items.Add(new TrailItem("week", $"Semana {week + 1}", "var(--color-neutral-700)"));
            for (var i = 0; i < PerWeek && sessions.Count > 0; i++)
            {
                var planned = sessions[(cursor + i) % sessions.Count];
                items.Add(new TrailItem("node", ClientAppText.Short(planned.Name), State: TrailNodeState.Locked, X: NextX(), SessionId: planned.Id,
                    Toast: "Bloqueado até chegar a vez dele"));
            }
        }

        if (TotalWeeks is { } weeks && weeks - week - 1 > 0)
        {
            var more = weeks - week - 1;
            items.Add(new TrailItem("gap", $"mais {more} {(more == 1 ? "semana" : "semanas")}"));
        }

        if (Details.EndDate is { } end)
        {
            items.Add(new TrailItem("node", string.Empty, State: TrailNodeState.Locked, Icon: "trophy", When: $"Fim do plano · {end:dd/MM}",
                Toast: $"O plano termina em {end:dd/MM}"));
        }

        return items;
    }

    /// <summary>The planned session the client does next: the one after the last workout done, in plan order.</summary>
    public PlannedSession? NextSession => Sessions.Count == 0 ? null : Sessions[NextSessionIndex(Sessions) % Sessions.Count];

    /// <summary>Whether the exercise was not in the same session of the previous version.</summary>
    public bool IsNew(PlannedSession session, PlannedExercise exercise) =>
        Previous?.Content.Sessions.FirstOrDefault(s => s.Name == session.Name) is { } before
        && before.Exercises.All(e => e.ExerciseId != exercise.ExerciseId);

    /// <summary>About how long the session takes: about 40 s per set plus the rests, rounded to 5 minutes.</summary>
    public static int Minutes(PlannedSession session)
    {
        if (session.Aerobic is { DurationMinutes: { } aerobic })
        {
            return aerobic;
        }

        var seconds = session.Exercises.Sum(e => e.Sets.Sum(s => (s.DurationSeconds ?? 40) + e.RestSeconds));
        return Math.Max(5, (int)Math.Round(seconds / 60.0 / 5) * 5);
    }

    private int NextSessionIndex(IReadOnlyList<PlannedSession> sessions)
    {
        if (LastCompleted is not { } last)
        {
            return 0;
        }

        var index = sessions.ToList().FindIndex(s => s.Name == last.PlannedSessionName);
        return index < 0 ? 0 : index + 1;
    }

    private int DoneInWeek(int week) => Completed.Count(w => WeekOf(DateOf(w)) == week);

    private int WeekOf(DateOnly date) => date < Details.StartDate ? 0 : (date.DayNumber - Details.StartDate.DayNumber) / 7 + 1;

    public static DateOnly DateOf(WorkoutSessionItem workout) =>
        workout.LocalDate ?? DateOnly.FromDateTime((workout.CompletedAt ?? workout.StartedAt).ToLocalTime().DateTime);
}

/// <summary>The client's published meal plan and today's diary, as the client app's "Alimentação" screen shows them.</summary>
public sealed class NutritionPreview
{
    public required MealPlanVersionDetail Version { get; init; }

    public required string DietitianName { get; init; }

    public required string OrganizationName { get; init; }

    /// <summary>Today's food diary entries; null when the professional cannot read the diary.</summary>
    public IReadOnlyList<FoodDiaryEntryItem>? Diary { get; init; }

    public required TimeOnly Now { get; init; }

    public IReadOnlyList<PlannedMeal> Meals => [.. Version.Content.Meals.OrderBy(m => m.Time ?? TimeOnly.MaxValue).ThenBy(m => m.Order)];

    public FoodDiaryEntryItem? EntryFor(PlannedMeal meal) =>
        Diary?.Where(e => e.PlannedMealId == meal.Id).OrderByDescending(e => e.EatenAt).FirstOrDefault();

    public int? WaterMl => Diary?.Sum(e => e.WaterMl ?? 0);

    /// <summary>The meal the client is asked about now: the first one not logged yet whose time is near or ahead.</summary>
    public PlannedMeal? NextMeal => Meals.FirstOrDefault(m => EntryFor(m) is null && (m.Time is not { } time || time >= Now.AddMinutes(-90)));

    /// <summary>How the client app words each adherence answer (the client's own words, never a judgement).</summary>
    public static string Adherence(string? value) => value switch
    {
        "AsPlanned" => "Segui o plano",
        "Partly" => "Segui em parte",
        "Different" => "Comi outra coisa",
        "Unplanned" => "Fora do plano",
        _ => "Registrado",
    };
}

public static class ClientAppText
{
    public static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>"A" for "Treino A", otherwise the first letter: the label inside a path node.</summary>
    public static string Short(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "?";
        }

        return parts.Length > 1 && parts[^1].Length <= 2 ? parts[^1].ToUpper(Brazil) : name.Trim()[..1].ToUpper(Brazil);
    }

    /// <summary>"ter 29/09".</summary>
    public static string Day(DateOnly date) => $"{Brazil.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek).TrimEnd('.')} {date:dd/MM}";

    /// <summary>"Carlos" for "Carlos Lima", "Dra. Marina" for "Dra. Marina Reis".</summary>
    public static string FirstName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => name,
            > 1 when parts[0].EndsWith('.') => $"{parts[0]} {parts[1]}",
            _ => parts[0],
        };
    }

    /// <summary>"granola sem açúcar" for "Granola sem açúcar", to read mid-sentence.</summary>
    public static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLower(text[0], Brazil) + text[1..];

    public static string Kg(decimal kg) => kg.ToString("0.##", Brazil);

    public static string Number(int value) => value.ToString("N0", Brazil);

    /// <summary>"4 × 8–12 · 42,5 kg · descanso 90 s".</summary>
    public static string ExerciseLine(PlannedExercise exercise)
    {
        var sets = exercise.Sets;
        if (sets.Count == 0)
        {
            return $"sem séries · descanso {exercise.RestSeconds} s";
        }

        var first = sets[0];
        var uniform = sets.All(s => s.MinRepetitions == first.MinRepetitions && s.MaxRepetitions == first.MaxRepetitions
                                    && s.DurationSeconds == first.DurationSeconds && s.SuggestedLoadKg == first.SuggestedLoadKg);
        var load = first.SuggestedLoadKg is { } kg ? $" · {Kg(kg)} kg" : string.Empty;
        var summary = uniform ? $"{sets.Count} × {Target(first)}{load}" : $"{sets.Count} séries";
        return $"{summary} · descanso {exercise.RestSeconds} s";
    }

    /// <summary>"8–12", "10" or "30 s".</summary>
    public static string Target(SetTarget set) => set.MinRepetitions is { } min
        ? (set.MaxRepetitions is { } max && max != min ? $"{min}–{max}" : $"{min}")
        : set.DurationSeconds is { } seconds ? $"{seconds} s" : "–";
}
