using System.Globalization;
using System.Text.Json;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Domain.Coaching;

namespace MyHealth.Application.Coaching;

/// <summary>The rules and tools given to the coach model. Tools only prepare suggestions; the trainer decides.</summary>
public static class CoachTools
{
    public const string ChangeLoad = "change_load";
    public const string ChangeSets = "change_sets";
    public const string ChangeRepetitions = "change_repetitions";
    public const string ChangeRest = "change_rest";
    public const string SwapExercise = "swap_exercise";
    public const string AddInstruction = "add_instruction";
    public const string TalkToClient = "talk_to_client";

    public const string SystemPrompt = """
        You are the MyHealth training coach, an assistant for a licensed personal trainer (CREF) in Brazil.
        You review how one client is responding to the training plan and suggest adjustments. The trainer decides; you suggest.
        Write in Brazilian Portuguese, for a fitness professional, briefly and concretely.

        Use only the JSON context: the plan draft, the sets the client logged in this plan (newest first), perceived exertion (RPE 0–10),
        adherence, discomfort reports, body checks and, when present, wellbeing (sleep and energy, 1–5).

        How to reason:
        - Progressive overload: when the client completed every prescribed set at the top of the repetition range with RPE ≤ 7 in the last
          two workouts of an exercise, suggest a small load increase (about 2.5–10%) or more repetitions.
        - When the client repeatedly fails the bottom of the range, or RPE ≥ 9, suggest reducing the load or the volume.
        - Low adherence: prefer making the plan easier to follow (fewer sets, shorter sessions) over adding work, and say so in the summary.
        - Poor sleep or low energy: avoid progressions this week.
        - Use swap_exercise only with an exercise id from "alternatives", e.g. when equipment or a recurring complaint suggests it.
        - With little or no data, say that it is too early to adjust and do not call tools.

        Rules you must never break:
        - No diagnosis, no assessment of pain or injuries, no medical, nutritional or medication advice.
        - If the client reported discomfort or a body part getting worse, never suggest a progression: call talk_to_client and say that the
          trainer should talk to the client and, if needed, refer them to a health professional.
        - Use only ids that appear in the context. Every tool call needs a short reason citing the data (dates, loads, RPE).
        - At most 8 tool calls. Your text reply is the summary for the trainer: 2–5 sentences, no lists of ids.
        """;

    public static readonly IReadOnlyList<AssistantToolDefinition> All =
    [
        Define(ChangeLoad, "Suggest a new load (kg) for every set of one planned exercise.", """
            {
              "type": "object",
              "properties": {
                "planned_exercise_id": { "type": "string" },
                "load_kg": { "type": "number", "minimum": 0 },
                "reason": { "type": "string" }
              },
              "required": ["planned_exercise_id", "load_kg", "reason"]
            }
            """),
        Define(ChangeSets, "Suggest a new number of sets for one planned exercise.", """
            {
              "type": "object",
              "properties": {
                "planned_exercise_id": { "type": "string" },
                "sets": { "type": "integer", "minimum": 1, "maximum": 10 },
                "reason": { "type": "string" }
              },
              "required": ["planned_exercise_id", "sets", "reason"]
            }
            """),
        Define(ChangeRepetitions, "Suggest a new repetition range for one planned exercise.", """
            {
              "type": "object",
              "properties": {
                "planned_exercise_id": { "type": "string" },
                "min_repetitions": { "type": "integer", "minimum": 1, "maximum": 50 },
                "max_repetitions": { "type": "integer", "minimum": 1, "maximum": 50 },
                "reason": { "type": "string" }
              },
              "required": ["planned_exercise_id", "min_repetitions", "max_repetitions", "reason"]
            }
            """),
        Define(ChangeRest, "Suggest a new rest time between sets for one planned exercise.", """
            {
              "type": "object",
              "properties": {
                "planned_exercise_id": { "type": "string" },
                "rest_seconds": { "type": "integer", "minimum": 0, "maximum": 600 },
                "reason": { "type": "string" }
              },
              "required": ["planned_exercise_id", "rest_seconds", "reason"]
            }
            """),
        Define(SwapExercise, "Suggest replacing one planned exercise with an exercise from alternatives, keeping the sets.", """
            {
              "type": "object",
              "properties": {
                "planned_exercise_id": { "type": "string" },
                "replacement_exercise_id": { "type": "string", "description": "An exerciseId from alternatives." },
                "reason": { "type": "string" }
              },
              "required": ["planned_exercise_id", "replacement_exercise_id", "reason"]
            }
            """),
        Define(AddInstruction, "Suggest a short execution cue to add to one planned exercise's instructions.", """
            {
              "type": "object",
              "properties": {
                "planned_exercise_id": { "type": "string" },
                "instruction": { "type": "string", "description": "In Portuguese, for the client, at most 200 characters." },
                "reason": { "type": "string" }
              },
              "required": ["planned_exercise_id", "instruction", "reason"]
            }
            """),
        Define(TalkToClient, "Recommend that the trainer talks to the client before changing the plan (discomfort, low adherence, poor recovery).", """
            {
              "type": "object",
              "properties": {
                "planned_exercise_id": { "type": "string", "description": "Optional: the exercise the conversation is about." },
                "reason": { "type": "string" }
              },
              "required": ["reason"]
            }
            """),
    ];

    private static AssistantToolDefinition Define(string name, string description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return new AssistantToolDefinition(name, description, document.RootElement.Clone());
    }
}

/// <summary>
/// Turns the model's tool calls into suggestions the trainer can apply, enforcing the rules on the server: ids from
/// the context only, bounded changes, no progression after recent discomfort. Anything else is dropped.
/// </summary>
public static class CoachSuggestionPolicy
{
    public const decimal MaxLoadChange = 0.15m;
    public const int MaxSetChange = 2;
    private const int MaxReasonLength = 500;

    public const string DiscomfortNotice =
        "O cliente relatou desconforto ou piora recente. Nenhuma progressão foi sugerida: converse com o cliente antes de aumentar a carga e, " +
        "se necessário, encaminhe para avaliação de um profissional de saúde.";

    public const string NoDataNotice = "Ainda há poucos treinos registrados neste plano para sugerir ajustes com segurança.";

    public const string DiscomfortReason = "Relato recente de desconforto ou piora: alinhe com o cliente antes de progredir.";

    public sealed record Outcome(IReadOnlyList<CoachSuggestion> Suggestions, IReadOnlyList<string> Notices, int Dropped);

    public static Outcome Apply(IReadOnlyList<AssistantToolCall> calls, CoachContext context, DateTimeOffset now)
    {
        var suggestions = new List<CoachSuggestion>();
        var dropped = 0;
        foreach (var call in calls)
        {
            var suggestion = Create(call, context, now);

            // Progressions are removed before duplicates, so an earlier progression never hides a later reduction.
            var blocked = suggestion is not null && context.HasRecentDiscomfort && suggestion.PlannedExerciseId is { } id
                          && context.FindPlannedExercise(id) is { } current && IsProgression(suggestion, current);
            if (suggestion is null || blocked || suggestions.Any(s => s.Kind == suggestion.Kind && s.PlannedExerciseId == suggestion.PlannedExerciseId))
            {
                dropped++;
                continue;
            }

            suggestions.Add(suggestion);
        }

        var notices = new List<string>();
        if (context.HasRecentDiscomfort)
        {
            notices.Add(DiscomfortNotice);
            if (!suggestions.Any(s => s.Kind == CoachSuggestionKind.TalkToClient))
            {
                suggestions.Insert(0, Suggestion(now, CoachSuggestionKind.TalkToClient, null, DiscomfortReason));
            }
        }

        if (context.ExerciseHistory.Count == 0 && context.RecentWorkouts.Count == 0)
        {
            notices.Add(NoDataNotice);
        }

        return new Outcome([.. suggestions.Take(CoachAnalysis.MaxSuggestions)], notices, dropped);
    }

    private static bool IsProgression(CoachSuggestion suggestion, CoachPlannedExercise current) => suggestion.Kind switch
    {
        CoachSuggestionKind.ChangeLoad => suggestion.LoadKg > (current.Sets.Max(s => s.SuggestedLoadKg) ?? 0),
        CoachSuggestionKind.ChangeSets => suggestion.Sets > current.Sets.Count,
        CoachSuggestionKind.ChangeRepetitions => suggestion.MaxRepetitions > (current.Sets.Max(s => s.MaxRepetitions) ?? 0),
        CoachSuggestionKind.ChangeRest => suggestion.RestSeconds < current.RestSeconds,
        _ => false,
    };

    private static CoachSuggestion? Create(AssistantToolCall call, CoachContext context, DateTimeOffset now)
    {
        var input = call.Input;
        if (input.ValueKind != JsonValueKind.Object || Text(input, "reason") is not { } reason)
        {
            return null;
        }

        reason = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;
        var plannedId = GuidOf(input, "planned_exercise_id");
        var planned = plannedId is { } id ? context.FindPlannedExercise(id) : null;

        if (call.Name == CoachTools.TalkToClient)
        {
            return Suggestion(now, CoachSuggestionKind.TalkToClient, planned, reason);
        }

        // Every other tool changes one exercise of the draft, which must be in the context.
        if (planned is null)
        {
            return null;
        }

        var session = context.FindSessionOf(planned.PlannedExerciseId);
        switch (call.Name)
        {
            case CoachTools.ChangeLoad:
                var load = Decimal(input, "load_kg");
                var currentLoad = planned.Sets.Max(s => s.SuggestedLoadKg);
                if (load is not (>= 0 and <= 500) || load == currentLoad)
                {
                    return null;
                }

                // Rounded to 0.5 kg; with a current load, the change is bounded (small steps, and a mistaken decimal never doubles a load).
                load = Math.Round(load.Value * 2, MidpointRounding.AwayFromZero) / 2;
                if (currentLoad is > 0 && Math.Abs(load.Value - currentLoad.Value) / currentLoad.Value > MaxLoadChange)
                {
                    return null;
                }

                return Suggestion(now, CoachSuggestionKind.ChangeLoad, planned, reason) with { LoadKg = load, PlannedSessionId = session?.PlannedSessionId };

            case CoachTools.ChangeSets:
                var sets = Int(input, "sets");
                if (sets is not (>= 1 and <= 10) || sets == planned.Sets.Count || planned.Sets.Count == 0 || Math.Abs(sets.Value - planned.Sets.Count) > MaxSetChange)
                {
                    return null;
                }

                return Suggestion(now, CoachSuggestionKind.ChangeSets, planned, reason) with { Sets = sets, PlannedSessionId = session?.PlannedSessionId };

            case CoachTools.ChangeRepetitions:
                var min = Int(input, "min_repetitions");
                var max = Int(input, "max_repetitions") ?? min;
                if (min is not (>= 1 and <= 50) || max is not (>= 1 and <= 50) || max < min || planned.Sets.All(s => s.MinRepetitions is null))
                {
                    return null;
                }

                return Suggestion(now, CoachSuggestionKind.ChangeRepetitions, planned, reason) with
                {
                    MinRepetitions = min, MaxRepetitions = max, PlannedSessionId = session?.PlannedSessionId,
                };

            case CoachTools.ChangeRest:
                var rest = Int(input, "rest_seconds");
                if (rest is not (>= 0 and <= 600) || rest == planned.RestSeconds)
                {
                    return null;
                }

                return Suggestion(now, CoachSuggestionKind.ChangeRest, planned, reason) with { RestSeconds = rest, PlannedSessionId = session?.PlannedSessionId };

            case CoachTools.SwapExercise:
                var replacementId = GuidOf(input, "replacement_exercise_id");
                var replacement = context.Alternatives.FirstOrDefault(a => a.ExerciseId == replacementId);
                if (replacement is null)
                {
                    return null;
                }

                return Suggestion(now, CoachSuggestionKind.SwapExercise, planned, reason) with
                {
                    ReplacementExerciseId = replacement.ExerciseId, ReplacementExerciseName = replacement.Name, PlannedSessionId = session?.PlannedSessionId,
                };

            case CoachTools.AddInstruction:
                var instruction = Text(input, "instruction");
                if (instruction is null || instruction.Length > 300)
                {
                    return null;
                }

                return Suggestion(now, CoachSuggestionKind.AddInstruction, planned, reason) with { Instruction = instruction, PlannedSessionId = session?.PlannedSessionId };

            default:
                return null;
        }
    }

    private static CoachSuggestion Suggestion(DateTimeOffset now, CoachSuggestionKind kind, CoachPlannedExercise? planned, string reason) =>
        new(Guid.CreateVersion7(now), kind, null, planned?.PlannedExerciseId, planned?.Name, null, null, null, null, null, null, null, null, reason);

    private static string? Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static Guid? GuidOf(JsonElement input, string name) => Guid.TryParse(Text(input, name), out var id) ? id : null;

    private static int? Int(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == Math.Floor(number) && Math.Abs(number) <= 100_000
            ? (int)number
            : null;

    private static decimal? Decimal(JsonElement input, string name) => input.TryGetProperty(name, out var value) switch
    {
        true when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) => number,
        true when value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
