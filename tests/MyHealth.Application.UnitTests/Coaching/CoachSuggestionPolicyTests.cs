using System.Text.Json;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Coaching;
using MyHealth.Domain.Coaching;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.UnitTests.Coaching;

/// <summary>The server-side rules applied to whatever the model returns: only known ids, bounded changes, no progression after discomfort.</summary>
public class CoachSuggestionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Squat = Guid.CreateVersion7();
    private static readonly Guid Lunge = Guid.CreateVersion7();
    private static readonly Guid Plank = Guid.CreateVersion7();

    private static CoachContext Context(bool discomfort = false)
    {
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var squat = new CoachPlannedExercise(Squat, Guid.CreateVersion7(), "Agachamento", ["quadríceps"], 90, null,
            [new(8, 12, null, 40m), new(8, 12, null, 40m), new(8, 12, null, 40m)]);
        var plank = new CoachPlannedExercise(Plank, Guid.CreateVersion7(), "Prancha", ["abdômen"], 45, null, [new(null, null, 30, null)]);
        var plan = new CoachPlan("Força", "Força", TrainingLevel.Beginner, 3, 4, 1, false,
            [new CoachPlannedSession(Guid.CreateVersion7(), "Treino A", SessionKind.Strength, [squat, plank], null)]);
        return new CoachContext(
            today, plan, new CoachAdherence(6, 12, [2, 2, 1, 1]),
            [new CoachWorkout(today.AddDays(-2), "Treino A", WorkoutStatus.Completed, 6, discomfort, discomfort ? "joelho" : null, null)],
            [new CoachExerciseHistory(Squat, "Agachamento", [new CoachExerciseWorkout(today.AddDays(-2), 6, [new CoachLoggedSet(1, 12, 40m, null, 6)])])],
            [], null, [new CoachAlternative(Lunge, "Afundo", ["quadríceps"], [], "Agachamento")]);
    }

    private static AssistantToolCall Call(string name, object input) => new(name, JsonSerializer.SerializeToElement(input));

    [Fact]
    public void Keeps_valid_suggestions_and_rounds_loads_to_half_kilos()
    {
        var outcome = CoachSuggestionPolicy.Apply(
        [
            Call(CoachTools.ChangeLoad, new { planned_exercise_id = Squat, load_kg = 42.3, reason = "Topo da faixa" }),
            Call(CoachTools.ChangeRest, new { planned_exercise_id = Squat, rest_seconds = 120, reason = "Recuperação" }),
            Call(CoachTools.SwapExercise, new { planned_exercise_id = Squat, replacement_exercise_id = Lunge, reason = "Variação" }),
        ], Context(), Now);

        outcome.Suggestions.Select(s => s.Kind).ShouldBe([CoachSuggestionKind.ChangeLoad, CoachSuggestionKind.ChangeRest, CoachSuggestionKind.SwapExercise]);
        outcome.Suggestions[0].LoadKg.ShouldBe(42.5m);
        outcome.Suggestions[0].ExerciseName.ShouldBe("Agachamento");
        outcome.Suggestions[2].ReplacementExerciseName.ShouldBe("Afundo");
        outcome.Dropped.ShouldBe(0);
    }

    [Fact]
    public void Drops_invented_ids_unbounded_changes_and_duplicates()
    {
        var outcome = CoachSuggestionPolicy.Apply(
        [
            Call(CoachTools.ChangeLoad, new { planned_exercise_id = Guid.NewGuid(), load_kg = 42, reason = "id inventado" }),
            Call(CoachTools.ChangeLoad, new { planned_exercise_id = Squat, load_kg = 60, reason = "mais de 15%" }),
            Call(CoachTools.SwapExercise, new { planned_exercise_id = Squat, replacement_exercise_id = Guid.NewGuid(), reason = "fora das alternativas" }),
            Call(CoachTools.ChangeSets, new { planned_exercise_id = Squat, sets = 8, reason = "salto grande" }),
            Call(CoachTools.ChangeRepetitions, new { planned_exercise_id = Plank, min_repetitions = 10, max_repetitions = 12, reason = "prancha é por tempo" }),
            Call(CoachTools.ChangeRest, new { planned_exercise_id = Squat, rest_seconds = 60, reason = "sem motivo" }),
            Call(CoachTools.ChangeRest, new { planned_exercise_id = Squat, rest_seconds = 75, reason = "duplicada" }),
            Call(CoachTools.ChangeRest, new { planned_exercise_id = Squat, rest_seconds = 75 }),
            Call("delete_plan", new { reason = "ferramenta desconhecida" }),
        ], Context(), Now);

        outcome.Suggestions.ShouldHaveSingleItem().RestSeconds.ShouldBe(60);
        outcome.Dropped.ShouldBe(8);
    }

    [Fact]
    public void After_discomfort_progressions_are_removed_and_a_conversation_is_proposed()
    {
        var outcome = CoachSuggestionPolicy.Apply(
        [
            Call(CoachTools.ChangeLoad, new { planned_exercise_id = Squat, load_kg = 44, reason = "progressão" }),
            Call(CoachTools.ChangeSets, new { planned_exercise_id = Squat, sets = 4, reason = "mais volume" }),
            Call(CoachTools.ChangeLoad, new { planned_exercise_id = Plank, load_kg = 5, reason = "sem carga atual" }),
            Call(CoachTools.ChangeSets, new { planned_exercise_id = Squat, sets = 2, reason = "menos volume" }),
        ], Context(discomfort: true), Now);

        outcome.Notices.ShouldContain(CoachSuggestionPolicy.DiscomfortNotice);
        outcome.Suggestions.Select(s => s.Kind).ShouldBe([CoachSuggestionKind.TalkToClient, CoachSuggestionKind.ChangeSets]);
        outcome.Suggestions[1].Sets.ShouldBe(2);
    }

    [Fact]
    public void Applying_a_suggestion_changes_only_the_targeted_exercise_of_the_draft()
    {
        var target = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var draft = new PlanContent(
        [
            new PlannedSession(Guid.CreateVersion7(), "Treino A", SessionKind.Strength, 1, null,
            [
                new PlannedExercise(target, Guid.CreateVersion7(), "Agachamento", 1, 1, null, 90, [], [new SetTarget(1, 8, 12, null, 40m), new SetTarget(2, 8, 12, null, 40m)]),
                new PlannedExercise(other, Guid.CreateVersion7(), "Remada", 1, 2, null, 60, [], [new SetTarget(1, 10, 12, null, 30m)]),
            ], null),
        ]);
        CoachSuggestion Suggestion(CoachSuggestionKind kind) =>
            new(Guid.CreateVersion7(), kind, null, target, "Agachamento", null, null, null, null, null, null, null, null, "motivo");

        var sets = CoachSuggestionApplier.Apply(draft, Suggestion(CoachSuggestionKind.ChangeSets) with { Sets = 4 }, null).Value;
        sets.Sessions[0].Exercises[0].Sets.Select(s => s.Order).ShouldBe([1, 2, 3, 4]);
        sets.Sessions[0].Exercises[1].ShouldBe(draft.Sessions[0].Exercises[1]);

        var reps = CoachSuggestionApplier.Apply(draft, Suggestion(CoachSuggestionKind.ChangeRepetitions) with { MinRepetitions = 6, MaxRepetitions = 8 }, null).Value;
        reps.Sessions[0].Exercises[0].Sets.ShouldAllBe(s => s.MinRepetitions == 6 && s.MaxRepetitions == 8);

        var gone = Suggestion(CoachSuggestionKind.ChangeRest) with { PlannedExerciseId = Guid.CreateVersion7(), RestSeconds = 30 };
        CoachSuggestionApplier.Apply(draft, gone, null).Error.ShouldBe(CoachErrors.PlanChanged);
    }
}
