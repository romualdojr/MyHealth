using System.Globalization;
using System.Text.Json;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Coaching;
using MyHealth.Application.Coaching;

namespace MyHealth.Infrastructure.Coaching;

/// <summary>
/// The coach without an AI provider: classic progression rules over the client's logged sets. Top of the range in the
/// last two workouts with RPE ≤ 7 → small load increase (or more repetitions without load); below the range or RPE ≥ 9
/// → load or volume down; recent discomfort or low adherence → talk to the client. The default model, used in
/// development and tests, and a sensible fallback where no AI provider is contracted.
/// </summary>
internal sealed class RuleBasedTrainingCoachModel : ITrainingCoachModel
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public string Name => "rules";

    public Task<CoachModelReply?> AnalyzeAsync(CoachModelRequest request, CancellationToken cancellationToken)
    {
        var context = request.Context;
        var calls = new List<AssistantToolCall>();
        var findings = new List<string>();

        var poorRecovery = context.Wellbeing is { Days: >= 3 } w && (w.AverageSleep < 2.5 || w.AverageEnergy < 2.5);
        if (poorRecovery)
        {
            findings.Add("O sono ou a energia relatados nas últimas duas semanas estão baixos; evitei progressões nesta semana.");
        }

        if (context.HasRecentDiscomfort)
        {
            calls.Add(Call(CoachTools.TalkToClient, new { reason = "Relato recente de desconforto ou piora: confirme como o cliente está antes de progredir." }));
            findings.Add("Houve relato recente de desconforto.");
        }

        var expected = context.Adherence.ExpectedLast28Days;
        if (expected > 0 && context.Plan.WeeksSinceStart >= 2 && context.Adherence.CompletedLast28Days * 2 < expected)
        {
            calls.Add(Call(CoachTools.TalkToClient, new
            {
                reason = $"Adesão baixa: {context.Adherence.CompletedLast28Days} de {expected} treinos previstos nas últimas 4 semanas. " +
                         "Vale entender a rotina do cliente; um plano mais curto pode ajudar.",
            }));
            findings.Add($"A adesão está baixa ({context.Adherence.CompletedLast28Days} de {expected} treinos em 4 semanas).");
        }

        foreach (var history in context.ExerciseHistory)
        {
            var planned = context.FindPlannedExercise(history.PlannedExerciseId);
            if (planned is null || planned.Sets.Count == 0 || history.Workouts.Count < 2)
            {
                continue;
            }

            var lastTwo = history.Workouts.Take(2).ToList();
            var load = planned.Sets.Max(s => s.SuggestedLoadKg);
            var dates = string.Join(" e ", lastTwo.Select(x => x.Date.ToString("dd/MM", Brazil)));
            var before = calls.Count;

            if (lastTwo.All(x => ReachedTop(x, planned)) && !poorRecovery && !context.HasRecentDiscomfort)
            {
                if (load is > 0)
                {
                    var next = Math.Max(load.Value + 0.5m, Math.Round(load.Value * 1.05m * 2, MidpointRounding.AwayFromZero) / 2);
                    calls.Add(Call(CoachTools.ChangeLoad, new
                    {
                        planned_exercise_id = planned.PlannedExerciseId,
                        load_kg = next,
                        reason = $"Completou todas as séries no topo da faixa em {dates} com esforço até 7: pronto para progredir de {Kg(load.Value)} para {Kg(next)}.",
                    }));
                }
                else if (planned.Sets.Max(s => s.MaxRepetitions) is { } max and < 20)
                {
                    calls.Add(Call(CoachTools.ChangeRepetitions, new
                    {
                        planned_exercise_id = planned.PlannedExerciseId,
                        min_repetitions = (planned.Sets.Min(s => s.MinRepetitions) ?? max) + 2,
                        max_repetitions = max + 2,
                        reason = $"Completou todas as séries no topo da faixa em {dates} com esforço até 7, sem carga prescrita: mais repetições.",
                    }));
                }

                if (calls.Count > before)
                {
                    findings.Add($"{planned.Name}: pronto para progredir.");
                }
            }
            else if (lastTwo.All(x => FellShort(x, planned)))
            {
                if (load is > 0)
                {
                    var lower = Math.Round(load.Value * 0.9m * 2, MidpointRounding.AwayFromZero) / 2;
                    calls.Add(Call(CoachTools.ChangeLoad, new
                    {
                        planned_exercise_id = planned.PlannedExerciseId,
                        load_kg = lower,
                        reason = $"Ficou abaixo da faixa prescrita ou com esforço 9+ em {dates}: reduzir de {Kg(load.Value)} para {Kg(lower)} para manter a técnica.",
                    }));
                }
                else if (planned.Sets.Count > 2)
                {
                    calls.Add(Call(CoachTools.ChangeSets, new
                    {
                        planned_exercise_id = planned.PlannedExerciseId,
                        sets = planned.Sets.Count - 1,
                        reason = $"Ficou abaixo da faixa prescrita ou com esforço 9+ em {dates}: uma série a menos por enquanto.",
                    }));
                }

                if (calls.Count > before)
                {
                    findings.Add($"{planned.Name}: está difícil demais no momento.");
                }
            }
        }

        string summary;
        if (context.ExerciseHistory.Count == 0 && context.RecentWorkouts.Count == 0)
        {
            summary = "Ainda não há treinos registrados neste plano. Assim que o cliente registrar pelo menos dois treinos, consigo comparar o realizado com o prescrito.";
        }
        else if (findings.Count == 0)
        {
            summary = $"Nas últimas semanas o cliente fez {context.Adherence.CompletedLast28Days} treinos deste plano e está dentro do prescrito. " +
                      "Nenhum ajuste necessário por enquanto.";
        }
        else
        {
            summary = string.Join(" ", findings);
        }

        return Task.FromResult<CoachModelReply?>(new CoachModelReply(summary, calls));
    }

    private static bool ReachedTop(CoachExerciseWorkout workout, CoachPlannedExercise planned)
    {
        if (workout.PerceivedExertion is > 7 || workout.Sets.Count < planned.Sets.Count || workout.Sets.Any(s => s.Difficulty is > 7))
        {
            return false;
        }

        return planned.Sets.Select((target, index) => (target, logged: workout.Sets.ElementAtOrDefault(index))).All(pair =>
            pair.logged is not null && (pair.target.MaxRepetitions is not { } max || pair.logged.Repetitions >= max)
            && (pair.target.DurationSeconds is not { } seconds || pair.logged.DurationSeconds >= seconds));
    }

    private static bool FellShort(CoachExerciseWorkout workout, CoachPlannedExercise planned) =>
        workout.PerceivedExertion is >= 9
        || planned.Sets.Select((target, index) => (target, logged: workout.Sets.ElementAtOrDefault(index))).Any(pair =>
            pair.logged is not null && pair.target.MinRepetitions is { } min && pair.logged.Repetitions < min);

    private static string Kg(decimal value) => $"{value.ToString("0.#", Brazil)} kg";

    private static AssistantToolCall Call(string name, object input) => new(name, JsonSerializer.SerializeToElement(input));
}
