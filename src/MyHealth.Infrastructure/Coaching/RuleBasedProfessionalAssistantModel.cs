using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.ProfessionalAssistant;
using MyHealth.Application.ProfessionalAssistant;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Professionals;

namespace MyHealth.Infrastructure.Coaching;

/// <summary>
/// The dietitian's and the therapy practitioner's chat without an AI provider. Understands summaries ("resumo", "como
/// está", "atenção") and plan requests ("monte", "crie", "proposta", "cardápio"): a meal plan from a template adapted to the
/// assessment (vegetarian, lactose, gluten, eggs, peanuts), or a care plan in the practitioner's first modality. Targets
/// the patients mentioned by alias, else the focused one, else everyone. Default model, used in development and tests.
/// </summary>
internal sealed partial class RuleBasedProfessionalAssistantModel : IProfessionalAssistantModel
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public string Name => "rules";

    public Task<ProfessionalAssistantModelReply?> RespondAsync(ProfessionalAssistantModelRequest request, CancellationToken cancellationToken)
    {
        var context = request.Context;
        var nutrition = context.Discipline == Discipline.Dietitian;
        if (context.Patients.Count == 0)
        {
            return Reply(
                "Nenhum paciente seu autorizou a análise por IA ainda. Eles podem ativar em Compartilhamento › Análise por IA no aplicativo.");
        }

        var message = request.Message.ToLowerInvariant();
        var mentioned = AliasPattern().Matches(request.Message).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = context.Patients
            .Where(p => mentioned.Count > 0 ? mentioned.Contains(p.Alias) : context.FocusAlias is null || p.Alias == context.FocusAlias)
            .ToList();

        if (Regex.IsMatch(message, "monte|montar|crie|criar|elabor|propo|sugir|sugest|novo plano|card[aá]pio"))
        {
            // Without a patient in view, plans go to those who have none yet.
            var forPlans = mentioned.Count > 0 || context.FocusAlias is not null ? targets : [.. targets.Where(p => !HasPlan(p))];
            return Task.FromResult<ProfessionalAssistantModelReply?>(nutrition ? MealPlans(forPlans, context) : CarePlans(forPlans, context));
        }

        if (Regex.IsMatch(message, "resum|como est|semana|vis[aã]o|geral|status|andamento|aten[cç]") || mentioned.Count > 0 || context.FocusAlias is not null)
        {
            var text = new StringBuilder();
            if (targets.Count > 1)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Resumo dos {targets.Count} pacientes que autorizaram a análise:");
            }

            foreach (var patient in targets)
            {
                text.AppendLine(nutrition ? NutritionLine(patient, context.Today) : TherapyLine(patient, context.Today));
            }

            if (context.PatientsWithoutConsent > 0 && context.FocusAlias is null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{context.PatientsWithoutConsent} paciente(s) não autorizaram a análise por IA e não aparecem aqui.");
            }

            return Reply(text.ToString().Trim());
        }

        return Reply(nutrition
            ? "Posso resumir o diário e a adesão dos seus pacientes e propor um plano alimentar com base na avaliação. " +
              "Experimente: \"resumo da semana\" ou \"monte um plano alimentar para o Paciente 1\"."
            : "Posso resumir as sessões e os planos de cuidado dos seus pacientes e propor um plano de cuidado. " +
              "Experimente: \"resumo\" ou \"proposta de plano para o Paciente 1\".");
    }

    private static Task<ProfessionalAssistantModelReply?> Reply(string text) =>
        Task.FromResult<ProfessionalAssistantModelReply?>(new ProfessionalAssistantModelReply(text, []));

    private static bool HasPlan(AssistantPatient patient) =>
        patient.Nutrition?.Plan is not null || patient.Therapy?.CarePlans.Any(p => p.Status == Domain.Therapies.CarePlanStatus.Active) == true;

    private static string NutritionLine(AssistantPatient patient, DateOnly today)
    {
        var n = patient.Nutrition!;
        var line = new StringBuilder($"• {patient.Alias}");
        line.Append(n.Plan is null ? ": sem plano alimentar." : $" — {n.Plan.Name}");
        var d = n.Last14Days;
        if (d.Meals > 0)
        {
            line.Append(CultureInfo.InvariantCulture,
                $": {d.Meals} refeições em {d.DaysWithEntries} dias (conforme {d.AsPlanned}, parcial {d.Partly}, diferente {d.Different}, fora do plano {d.Unplanned})");
        }
        else if (n.Plan is not null)
        {
            line.Append(": sem registros no diário em 14 dias");
        }

        if (d.AverageWaterMlPerDay is { } water)
        {
            line.Append(CultureInfo.InvariantCulture, $"; água {water} ml/dia");
        }

        line.Append('.');
        if (n.LatestAssessment?.NextFollowUpOn is { } followUp)
        {
            line.Append(followUp < today ? $" Retorno atrasado desde {followUp.ToString("dd/MM", Brazil)}." : $" Retorno em {followUp.ToString("dd/MM", Brazil)}.");
        }

        var unplanned = n.RecentEntries.FirstOrDefault(e => e.Adherence == MealAdherence.Unplanned && e.Description is not null);
        if (unplanned is not null)
        {
            line.Append(CultureInfo.InvariantCulture, $" Fora do plano em {unplanned.Date.ToString("dd/MM", Brazil)}: {unplanned.Description}");
        }

        return line.ToString();
    }

    private static string TherapyLine(AssistantPatient patient, DateOnly today)
    {
        var t = patient.Therapy!;
        var active = t.CarePlans.FirstOrDefault(p => p.Status == Domain.Therapies.CarePlanStatus.Active);
        var line = new StringBuilder($"• {patient.Alias}");
        line.Append(active is null ? ": sem plano de cuidado ativo" : $" — {active.ModalityCode}: {active.Goals}");
        line.Append(CultureInfo.InvariantCulture, $"; {t.CompletedSessionsLast90Days} sessão(ões) em 90 dias, {t.ScheduledSessions} agendada(s).");
        if (active?.NextReviewOn is { } review)
        {
            line.Append(review < today ? $" Revisão atrasada desde {review.ToString("dd/MM", Brazil)}." : $" Revisão em {review.ToString("dd/MM", Brazil)}.");
        }

        var feelings = t.RecentSessions.Where(s => s.ClientWellbeing is not null).Select(s => s.ClientWellbeing!.Value).ToList();
        if (feelings.Count > 0)
        {
            line.Append(CultureInfo.InvariantCulture, $" Bem-estar relatado: {feelings.Average().ToString("0.#", Brazil)}/5.");
        }

        if (t.RecentSessions.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.AdverseEvents)) is { } adverse)
        {
            line.Append(CultureInfo.InvariantCulture, $" Evento adverso em {adverse.Date.ToString("dd/MM", Brazil)}: {adverse.AdverseEvents}");
        }

        return line.ToString();
    }

    private static ProfessionalAssistantModelReply MealPlans(List<AssistantPatient> patients, ProfessionalAssistantContext context)
    {
        if (patients.Count == 0)
        {
            return new ProfessionalAssistantModelReply("Todos os pacientes já têm um plano. Escolha um paciente para eu propor uma nova versão.", []);
        }

        var text = new StringBuilder();
        var calls = new List<AssistantToolCall>();
        foreach (var patient in patients.Take(3))
        {
            var assessment = patient.Nutrition?.LatestAssessment;
            // Restrictions and allergies only: preferences ("gosta de ovos") are likes, not limits.
            var avoid = $"{assessment?.Restrictions} {assessment?.Allergies}".ToLowerInvariant();
            var allergyText = assessment?.Allergies?.ToLowerInvariant() ?? string.Empty;
            var vegan = avoid.Contains("vegan", StringComparison.Ordinal);
            var vegetarian = vegan || avoid.Contains("vegetarian", StringComparison.Ordinal);
            var noLactose = vegan || avoid.Contains("lactose", StringComparison.Ordinal) || avoid.Contains("leite", StringComparison.Ordinal);
            var noGluten = avoid.Contains("glúten", StringComparison.Ordinal) || avoid.Contains("gluten", StringComparison.Ordinal) || avoid.Contains("celíac", StringComparison.Ordinal);
            var noEggs = vegan || Regex.IsMatch(allergyText, @"\bovos?\b");

            object Protein(string fallback) =>
                noEggs ? Item(vegetarian ? "Tofu mexido" : "Queijo branco", vegetarian ? 100 : 30, "g", null, [])
                    : Item(fallback, 2, "unidade", null, []);

            var meals = new List<object>
            {
                Meal("Café da manhã", "07:00", [
                    noGluten ? Item("Tapioca", null, null, "3 colheres de sopa", ["Cuscuz (1 xícara)"]) : Item("Pão integral", null, null, "2 fatias", ["Tapioca (3 colheres de sopa)"]),
                    Protein("Ovos mexidos"),
                    Item(noLactose ? "Café com bebida vegetal" : "Café com leite", 200, "ml", null, []),
                    Item("Fruta da estação", 1, "unidade", null, ["Mamão (1 fatia)", "Banana (1 unidade)"]),
                ]),
                Meal("Almoço", "12:30", [
                    Item("Arroz integral", 100, "g", "4 colheres de sopa", ["Quinoa (100 g)"]),
                    Item("Feijão", null, null, "1 concha", ["Lentilha (1 concha)", "Grão-de-bico (1 concha)"]),
                    vegetarian ? Item("Tofu grelhado", 120, "g", null, ["Grão-de-bico (1 concha extra)"]) : Item("Frango grelhado", 120, "g", null, ["Carne magra (120 g)", "Peixe (120 g)"]),
                    Item("Legumes cozidos", null, null, "3 colheres de sopa", []),
                    Item("Salada de folhas", null, null, "À vontade", []),
                ]),
                Meal("Lanche", "16:00", [
                    Item(vegan ? "Iogurte vegetal" : noLactose ? "Iogurte sem lactose" : "Iogurte natural", 170, "g", null, []),
                    Item("Aveia em flocos", 20, "g", "1 colher de sopa", []),
                ]),
                Meal("Jantar", "20:00", [
                    noEggs ? Item(vegetarian ? "Tofu grelhado" : "Peito de frango", 100, "g", null, []) : Item("Omelete de legumes", null, null, "2 ovos", []),
                    Item("Batata-doce", 100, "g", null, ["Mandioca (100 g)"]),
                    Item("Salada de folhas", null, null, "À vontade", []),
                ]),
            };

            var adjusted = new[] { vegetarian ? "vegetariano" : null, noLactose ? "sem lactose" : null, noGluten ? "sem glúten" : null, noEggs ? "sem ovos" : null }
                .OfType<string>().ToList();
            var reason = assessment is null
                ? "Sem avaliação registrada: modelo básico de 4 refeições. Confirme rotina, restrições e alergias antes de publicar."
                : $"Baseado na avaliação de {assessment.AssessedOn.ToString("dd/MM", Brazil)}" +
                  (assessment.Goals is { } goals ? $" (objetivo: {goals})" : string.Empty) +
                  (adjusted.Count > 0 ? $"; ajustado: {string.Join(", ", adjusted)}." : ".") +
                  (assessment.Allergies is { } allergies ? $" Confira as alergias relatadas ({allergies})." : string.Empty);

            calls.Add(new AssistantToolCall(ProfessionalAssistantTools.DraftMealPlan, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["patient"] = patient.Alias,
                ["name"] = "Plano alimentar — proposta",
                ["goal"] = assessment?.Goals,
                ["start_date"] = context.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["notes"] = "Beba 2 litros de água por dia.",
                ["meals"] = meals,
                ["reason"] = reason,
            })));
            text.AppendLine(CultureInfo.InvariantCulture, $"{patient.Alias}: proposta de 4 refeições{(adjusted.Count > 0 ? $" ({string.Join(", ", adjusted)})" : string.Empty)}.");
        }

        text.Append("Revise a proposta, crie o rascunho e ajuste no editor antes de publicar.");
        return new ProfessionalAssistantModelReply(text.ToString(), calls);
    }

    private static ProfessionalAssistantModelReply CarePlans(List<AssistantPatient> patients, ProfessionalAssistantContext context)
    {
        if (context.Modalities is not { Count: > 0 } modalities)
        {
            return new ProfessionalAssistantModelReply(
                "Você ainda não tem uma modalidade habilitada com qualificação verificada; por isso não posso propor um plano de cuidado.", []);
        }

        if (patients.Count == 0)
        {
            return new ProfessionalAssistantModelReply("Todos os pacientes já têm um plano de cuidado ativo. Escolha um paciente para revisar o plano.", []);
        }

        var text = new StringBuilder();
        var calls = new List<AssistantToolCall>();
        foreach (var patient in patients.Take(3))
        {
            var sessions = patient.Therapy?.RecentSessions ?? [];
            if (sessions.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.AdverseEvents)) is { } adverse)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"{patient.Alias}: há evento adverso registrado em {adverse.Date.ToString("dd/MM", Brazil)}. Revise o caso e, se necessário, encaminhe para avaliação médica antes de um novo plano.");
                continue;
            }

            var modality = modalities[0];
            var concern = sessions.Select(s => s.ReportedConcern).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            calls.Add(new AssistantToolCall(ProfessionalAssistantTools.ProposeCarePlan, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["patient"] = patient.Alias,
                ["modality_code"] = modality.Code,
                ["goals"] = concern is null
                    ? "Definir com o paciente a queixa principal e acompanhar o bem-estar relatado a cada sessão."
                    : $"Aliviar a queixa relatada ({concern}) e acompanhar o bem-estar relatado a cada sessão.",
                ["planned_sessions"] = 8,
                ["frequency"] = "1 vez por semana",
                ["next_review_on"] = context.Today.AddDays(28).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["reason"] = sessions.Count == 0
                    ? "Sem sessões registradas: proposta inicial padrão, a confirmar na primeira consulta."
                    : $"{sessions.Count} sessão(ões) recentes registradas; revisão em 4 semanas para avaliar a evolução.",
            })));
            text.AppendLine(CultureInfo.InvariantCulture, $"{patient.Alias}: proposta de {modality.Name}, 8 sessões semanais com revisão em 4 semanas.");
        }

        if (calls.Count > 0)
        {
            text.Append("Revise a proposta e inicie o plano de cuidado se fizer sentido.");
        }

        return new ProfessionalAssistantModelReply(text.ToString().Trim(), calls);
    }

    private static Dictionary<string, object?> Meal(string name, string time, IReadOnlyList<object> items) =>
        new() { ["name"] = name, ["time"] = time, ["items"] = items };

    private static Dictionary<string, object?> Item(string food, decimal? quantity, string? unit, string? householdMeasure, IReadOnlyList<string> substitutions) =>
        new()
        {
            ["food"] = food, ["quantity"] = quantity, ["unit"] = unit, ["household_measure"] = householdMeasure, ["substitutions"] = substitutions,
        };

    [GeneratedRegex(@"Paciente \d+", RegexOptions.IgnoreCase)]
    private static partial Regex AliasPattern();
}
