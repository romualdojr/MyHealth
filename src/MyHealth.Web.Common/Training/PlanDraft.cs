using System.Text.Json;
using MyHealth.Web.Common.Api;

namespace MyHealth.Web.Common.Training;

// Mutable editing state for the plan editor. The API's plan content is immutable; the editor copies it into these
// classes, lets the trainer change them, and sends them back as the bodies of PUT /training-plans/{id} and
// PUT /training-plans/{id}/sessions. Existing ids are sent back so progress stays linked across versions; new items go
// without an id and the API assigns one (the editor reloads the plan after every save).

/// <summary>The body of <c>PUT /training-plans/{id}</c> and of the details in <c>POST /training-plans</c>.</summary>
public sealed record PlanDetailsBody(
    string Name, string Goal, string Level, DateOnly StartDate, DateOnly? EndDate, int SuggestedSessionsPerWeek, string? Notes);

public sealed record SetTargetBody(int? MinRepetitions, int? MaxRepetitions, int? DurationSeconds, decimal? SuggestedLoadKg);

public sealed record PlannedExerciseBody(
    Guid? Id, Guid ExerciseId, string? Instructions, int RestSeconds, IReadOnlyList<Guid> AlternativeExerciseIds, IReadOnlyList<SetTargetBody> Sets);

public sealed record PlannedSessionBody(
    Guid? Id, string Name, string Kind, string? Notes, IReadOnlyList<PlannedExerciseBody> Exercises, AerobicTarget? Aerobic);

public sealed class PlanDetailsForm
{
    public string Name { get; set; } = string.Empty;

    public string Goal { get; set; } = string.Empty;

    public string Level { get; set; } = "Beginner";

    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public DateOnly? EndDate { get; set; }

    public int SuggestedSessionsPerWeek { get; set; } = 3;

    public string? Notes { get; set; }

    public static PlanDetailsForm From(PlanDetails details) => new()
    {
        Name = details.Name,
        Goal = details.Goal,
        Level = details.Level,
        StartDate = details.StartDate,
        EndDate = details.EndDate,
        SuggestedSessionsPerWeek = details.SuggestedSessionsPerWeek,
        Notes = details.Notes,
    };

    public PlanDetailsBody ToBody() =>
        new(Name.Trim(), Goal.Trim(), Level, StartDate, EndDate, SuggestedSessionsPerWeek, string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim());
}

public sealed class SetDraft
{
    /// <summary>Repetitions (a range) or a duration (seconds, e.g. a plank).</summary>
    public bool ByDuration { get; set; }

    public int? MinRepetitions { get; set; }

    public int? MaxRepetitions { get; set; }

    public int? DurationSeconds { get; set; }

    public decimal? SuggestedLoadKg { get; set; }

    public SetDraft Clone() => (SetDraft)MemberwiseClone();

    public SetTargetBody ToBody() => ByDuration
        ? new(null, null, DurationSeconds, SuggestedLoadKg)
        : new(MinRepetitions, MaxRepetitions ?? MinRepetitions, null, SuggestedLoadKg);
}

public sealed class ExerciseDraft
{
    public Guid? Id { get; init; }

    public Guid ExerciseId { get; init; }

    public string ExerciseName { get; init; } = string.Empty;

    public string? Instructions { get; set; }

    public int RestSeconds { get; set; } = 60;

    public List<Guid> AlternativeExerciseIds { get; init; } = [];

    public List<SetDraft> Sets { get; init; } = [];

    /// <summary>A new exercise with a sensible starting prescription: 3 × 10–12, 60 s rest.</summary>
    public static ExerciseDraft New(ExerciseItem exercise) => new()
    {
        ExerciseId = exercise.Id,
        ExerciseName = exercise.Name,
        Sets = [.. Enumerable.Range(0, 3).Select(_ => new SetDraft { MinRepetitions = 10, MaxRepetitions = 12 })],
    };

    /// <summary>Adds a set like the last one (or a first default set).</summary>
    public void AddSet() => Sets.Add(Sets.Count > 0 ? Sets[^1].Clone() : new SetDraft { MinRepetitions = 10, MaxRepetitions = 12 });

    public PlannedExerciseBody ToBody() =>
        new(Id, ExerciseId, string.IsNullOrWhiteSpace(Instructions) ? null : Instructions.Trim(), RestSeconds, AlternativeExerciseIds,
            [.. Sets.Select(s => s.ToBody())]);
}

public sealed class AerobicDraft
{
    public string Activity { get; set; } = string.Empty;

    public int? DurationMinutes { get; set; }

    public decimal? DistanceKm { get; set; }

    public string? Intensity { get; set; }

    public string? WarmUp { get; set; }

    public string? CoolDown { get; set; }

    public AerobicTarget ToTarget() => new(Activity.Trim(), DurationMinutes, DistanceKm, Blank(Intensity), Blank(WarmUp), Blank(CoolDown));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class SessionDraft
{
    public Guid? Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public string Kind { get; init; } = "Strength";

    public string? Notes { get; set; }

    public List<ExerciseDraft> Exercises { get; init; } = [];

    public AerobicDraft? Aerobic { get; init; }

    public bool IsAerobic => Kind == "Aerobic";

    public PlannedSessionBody ToBody() =>
        new(Id, Name.Trim(), Kind, string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            IsAerobic ? [] : [.. Exercises.Select(e => e.ToBody())],
            IsAerobic ? Aerobic?.ToTarget() : null);
}

/// <summary>The sessions of a plan's draft as the trainer edits them.</summary>
public sealed class PlanDraft
{
    public List<SessionDraft> Sessions { get; init; } = [];

    public static PlanDraft From(PlanContent content) => new()
    {
        Sessions =
        [
            .. content.Sessions.OrderBy(s => s.Order).Select(s => new SessionDraft
            {
                Id = s.Id,
                Name = s.Name,
                Kind = s.Kind,
                Notes = s.Notes,
                Exercises =
                [
                    .. s.Exercises.OrderBy(e => e.Order).Select(e => new ExerciseDraft
                    {
                        Id = e.Id,
                        ExerciseId = e.ExerciseId,
                        ExerciseName = e.ExerciseName,
                        Instructions = e.Instructions,
                        RestSeconds = e.RestSeconds,
                        AlternativeExerciseIds = [.. e.AlternativeExerciseIds],
                        Sets =
                        [
                            .. e.Sets.OrderBy(t => t.Order).Select(t => new SetDraft
                            {
                                ByDuration = t.MinRepetitions is null && t.DurationSeconds is not null,
                                MinRepetitions = t.MinRepetitions,
                                MaxRepetitions = t.MaxRepetitions,
                                DurationSeconds = t.DurationSeconds,
                                SuggestedLoadKg = t.SuggestedLoadKg,
                            }),
                        ],
                    }),
                ],
                Aerobic = s.Aerobic is { } a
                    ? new AerobicDraft
                    {
                        Activity = a.Activity, DurationMinutes = a.DurationMinutes, DistanceKm = a.DistanceKm, Intensity = a.Intensity,
                        WarmUp = a.WarmUp, CoolDown = a.CoolDown,
                    }
                    : null,
            }),
        ],
    };

    /// <summary>Adds a session named "Treino A", "Treino B"… (or "Aeróbico 1"…) and returns it.</summary>
    public SessionDraft AddSession(string kind)
    {
        var aerobic = kind == "Aerobic";
        var count = Sessions.Count(s => s.IsAerobic == aerobic);
        var session = new SessionDraft
        {
            Kind = kind,
            Name = aerobic ? $"Aeróbico {count + 1}" : $"Treino {(char)('A' + (count % 26))}",
            Aerobic = aerobic ? new AerobicDraft { Activity = "Caminhada", DurationMinutes = 30, Intensity = "Moderada" } : null,
        };
        Sessions.Add(session);
        return session;
    }

    public IReadOnlyList<PlannedSessionBody> ToBody() => [.. Sessions.Select(s => s.ToBody())];

    /// <summary>A stable text form of what would be sent, to tell whether there are unsaved changes.</summary>
    public string Snapshot() => JsonSerializer.Serialize(ToBody(), MyHealthApiClient.Json);

    /// <summary>
    /// What still blocks publishing, in the trainer's words. The API applies the same rules (and is the authority);
    /// listing them here lets the editor say what to fix before the trainer presses "Publicar".
    /// </summary>
    public IReadOnlyList<string> PublishingIssues()
    {
        var issues = new List<string>();
        if (Sessions.Count == 0)
        {
            issues.Add("Adicione pelo menos um treino.");
        }

        foreach (var session in Sessions)
        {
            var name = string.IsNullOrWhiteSpace(session.Name) ? "Treino sem nome" : session.Name;
            if (string.IsNullOrWhiteSpace(session.Name))
            {
                issues.Add("Dê um nome a cada treino.");
            }

            if (session.IsAerobic)
            {
                if (string.IsNullOrWhiteSpace(session.Aerobic?.Activity))
                {
                    issues.Add($"{name}: informe a atividade aeróbica.");
                }

                continue;
            }

            if (session.Exercises.Count == 0)
            {
                issues.Add($"{name}: adicione exercícios.");
            }

            foreach (var exercise in session.Exercises)
            {
                if (exercise.Sets.Count == 0)
                {
                    issues.Add($"{name} · {exercise.ExerciseName}: adicione séries.");
                }
                else if (exercise.Sets.Any(s => s.ByDuration ? s.DurationSeconds is not > 0 : s.MinRepetitions is not > 0))
                {
                    issues.Add($"{name} · {exercise.ExerciseName}: cada série precisa de repetições ou duração.");
                }
                else if (exercise.Sets.Any(s => !s.ByDuration && s.MaxRepetitions < s.MinRepetitions))
                {
                    issues.Add($"{name} · {exercise.ExerciseName}: o máximo de repetições é menor que o mínimo.");
                }
            }
        }

        return [.. issues.Distinct()];
    }
}

/// <summary>Moves an item one place up or down in a list (no-op at the ends).</summary>
public static class ListMoves
{
    public static void Move<T>(this List<T> items, T item, int delta)
    {
        var from = items.IndexOf(item);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= items.Count)
        {
            return;
        }

        items.RemoveAt(from);
        items.Insert(to, item);
    }
}
