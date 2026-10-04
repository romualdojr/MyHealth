using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Coaching;
using MyHealth.Application.Abstractions.TrainerAssistant;
using MyHealth.Application.Coaching;
using MyHealth.Application.TrainerAssistant;

namespace MyHealth.Infrastructure.Coaching;

/// <summary>
/// The trainer's chat without an AI provider. Understands three things: summaries ("resumo", "como está", "semana"),
/// evolving exercises ("evoluir", "progredir", "ajustar", "carga", "sugestões" — the coach's progression rules), and
/// which students the message is about (aliases mentioned, else the focused student, else everyone). Default model,
/// used in development and tests.
/// </summary>
internal sealed partial class RuleBasedTrainerAssistantModel : ITrainerAssistantModel
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");
    private readonly RuleBasedTrainingCoachModel _coach = new();

    public string Name => "rules";

    public async Task<TrainerAssistantModelReply?> RespondAsync(TrainerAssistantModelRequest request, CancellationToken cancellationToken)
    {
        var context = request.Context;
        var message = request.Message.ToLowerInvariant();
        var mentioned = AliasPattern().Matches(request.Message).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = context.Students
            .Where(s => mentioned.Count > 0 ? mentioned.Contains(s.Alias) : context.FocusAlias is null || s.Alias == context.FocusAlias)
            .ToList();

        if (context.Students.Count == 0)
        {
            return new TrainerAssistantModelReply(
                "Nenhum aluno seu autorizou a análise por IA ainda. Eles podem ativar em Compartilhamento › Análise por IA no aplicativo.", []);
        }

        var wantsEvolution = Regex.IsMatch(message, "evolu|progred|progress|ajust|carga|sugest|melhorar");
        var wantsSummary = Regex.IsMatch(message, "resum|como est|semana|vis[aã]o|geral|status|andamento");

        if (wantsEvolution)
        {
            var text = new StringBuilder();
            var calls = new List<AssistantToolCall>();
            foreach (var student in targets)
            {
                if (student.Training is null)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{student.Alias}: sem plano ativo para ajustar.");
                    continue;
                }

                var reply = await _coach.AnalyzeAsync(
                    new CoachModelRequest(CoachTools.SystemPrompt, student.Training, string.Empty, null, CoachTools.All), cancellationToken);
                // Conversations are not tied to an exercise: name the student so the server knows whose they are.
                calls.AddRange(reply!.ToolCalls.Select(call => call.Name == CoachTools.TalkToClient ? WithStudent(call, student.Alias) : call));
                text.AppendLine(CultureInfo.InvariantCulture, $"{student.Alias}: {reply.Summary}");
            }

            text.Append(calls.Count > 0 ? "As sugestões estão abaixo; aplique as que fizerem sentido e publique o plano." : "Nenhum ajuste a sugerir agora.");
            return new TrainerAssistantModelReply(text.ToString().Trim(), calls);
        }

        if (wantsSummary || mentioned.Count > 0 || context.FocusAlias is not null)
        {
            var text = new StringBuilder();
            if (targets.Count > 1)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Resumo dos {targets.Count} alunos que autorizaram a análise:");
            }

            foreach (var student in targets)
            {
                text.AppendLine(Summary(student, detailed: targets.Count == 1));
            }

            var attention = targets.Where(s => s.RecentDiscomfort || s.PendingFeedback > 0).Select(s => s.Alias).ToList();
            if (targets.Count > 1 && attention.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Atenção primeiro: {string.Join(", ", attention)}.");
            }

            if (context.StudentsWithoutConsent > 0 && context.FocusAlias is null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{context.StudentsWithoutConsent} aluno(s) não autorizaram a análise por IA e não aparecem aqui.");
            }

            return new TrainerAssistantModelReply(text.ToString().Trim(), []);
        }

        return new TrainerAssistantModelReply(
            "Posso resumir a semana de todos os alunos ou de um aluno, e sugerir como evoluir os exercícios de cada um. " +
            "Experimente: \"resumo da semana\" ou \"como evoluir os exercícios do Aluno 1\".", []);
    }

    private static string Summary(TrainerAssistantStudent s, bool detailed)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"• {s.Alias}");
        line.Append(s.PlanName is null ? ": sem plano ativo." : $" — {s.PlanName}: {s.CompletedLast28Days} de {s.ExpectedLast28Days} treinos em 4 semanas");
        if (s.PlanName is not null)
        {
            line.Append(s.LastWorkout is { } last ? $"; último em {last.ToString("dd/MM", Brazil)}" : "; nenhum treino recente");
            if (s.AverageExertion14Days is { } rpe)
            {
                line.Append(CultureInfo.InvariantCulture, $"; esforço médio {rpe.ToString("0.#", Brazil)}/10");
            }

            line.Append('.');
        }

        if (s.RecentDiscomfort)
        {
            line.Append(" Relatou desconforto recentemente.");
        }

        if (s.PendingFeedback > 0)
        {
            line.Append(CultureInfo.InvariantCulture, $" {s.PendingFeedback} feedback(s) aguardando sua resposta.");
        }

        if (detailed && s.Training is { } training)
        {
            foreach (var exercise in training.ExerciseHistory)
            {
                var last = exercise.Workouts[0];
                var best = last.Sets.Where(x => x.LoadKg is not null).Select(x => $"{x.Repetitions}×{x.LoadKg!.Value.ToString("0.#", Brazil)} kg").FirstOrDefault()
                           ?? last.Sets.Select(x => x.Repetitions is { } r ? $"{r} reps" : $"{x.DurationSeconds}s").FirstOrDefault();
                line.Append(CultureInfo.InvariantCulture, $"\n   – {exercise.Name}: último treino {best} ({exercise.Workouts.Count} registro(s) recentes)");
            }

            if (training.Wellbeing is { Days: > 0 } wellbeing)
            {
                line.Append(CultureInfo.InvariantCulture,
                    $"\n   – Bem-estar (14 dias): sono {wellbeing.AverageSleep?.ToString("0.#", Brazil) ?? "—"}/5, energia {wellbeing.AverageEnergy?.ToString("0.#", Brazil) ?? "—"}/5");
            }
        }

        return line.ToString();
    }

    private static AssistantToolCall WithStudent(AssistantToolCall call, string alias)
    {
        var input = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(call.Input.GetRawText()) ?? [];
        input["student"] = System.Text.Json.JsonSerializer.SerializeToElement(alias);
        return call with { Input = System.Text.Json.JsonSerializer.SerializeToElement(input) };
    }

    [GeneratedRegex(@"Aluno \d+", RegexOptions.IgnoreCase)]
    private static partial Regex AliasPattern();
}
