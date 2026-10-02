using System.Globalization;
using System.Text;
using System.Text.Json;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Assistant;

namespace MyHealth.Infrastructure.ClientExperience;

/// <summary>
/// A rule-based stand-in for the language model, for development and tests. It understands short Portuguese phrases:
/// a set during a workout ("feito, dez com quarenta e dois e meio"), a meal ("almocei arroz e frango, mas troquei a
/// salada"), a pain ("ontem doeu o joelho direito"), the last load of an exercise and the next meal. It follows the same
/// contract as the real model: answers in text and asks for records through tool calls.
/// </summary>
internal sealed class FakeAssistantModel : IAssistantModel
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly (string Stem, string Meal)[] Meals =
        [("almo", "Almoço"), ("jant", "Jantar"), ("cafe", "Café da manhã"), ("lanch", "Lanche"), ("ceia", "Ceia")];

    private static readonly string[] BodyParts =
        ["joelho", "ombro", "lombar", "costas", "coluna", "tornozelo", "quadril", "punho", "cotovelo", "pescoco", "panturrilha", "coxa", "pe", "mao"];

    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["um"] = 1, ["uma"] = 1, ["dois"] = 2, ["duas"] = 2, ["tres"] = 3, ["quatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["sete"] = 7, ["oito"] = 8,
        ["nove"] = 9, ["dez"] = 10, ["onze"] = 11, ["doze"] = 12, ["treze"] = 13, ["catorze"] = 14, ["quatorze"] = 14, ["quinze"] = 15,
        ["dezesseis"] = 16, ["dezessete"] = 17, ["dezoito"] = 18, ["dezenove"] = 19, ["vinte"] = 20, ["trinta"] = 30, ["quarenta"] = 40,
        ["cinquenta"] = 50, ["sessenta"] = 60, ["setenta"] = 70, ["oitenta"] = 80, ["noventa"] = 90, ["cem"] = 100, ["cento"] = 100,
    };

    public string Name => "fake";

    public Task<AssistantModelReply?> RespondAsync(AssistantModelRequest request, CancellationToken cancellationToken)
    {
        var text = Normalize(request.Message);
        var context = request.Context;
        var replies = new List<string>();
        var calls = new List<AssistantToolCall>();

        if (context.Training?.CurrentWorkout is { } workout && LooksLikeSet(text) && ParseNumbers(text) is { Count: > 0 } numbers
            && (workout.CurrentExerciseId ?? (workout.Exercises.Count > 0 ? workout.Exercises[0].ExerciseId : null)) is { } exerciseId)
        {
            var exercise = workout.Exercises.First(e => e.ExerciseId == exerciseId);
            var setNumber = workout.CurrentSetNumber ?? exercise.SetsLogged + 1;
            var reps = (int)numbers[0];
            decimal? load = numbers.Count > 1 ? numbers[1] : null;
            calls.Add(Call(AssistantTools.LogSet, new { exercise_id = exerciseId, set_number = setNumber, repetitions = reps, load_kg = load }));
            replies.Add(load is { } kg
                ? $"Série {setNumber}: {reps} repetições com {kg.ToString("0.##", Brazil)} kg. Registro?"
                : $"Série {setNumber}: {reps} repetições. Registro?");
        }
        else if (Meals.FirstOrDefault(m => text.Contains(m.Stem, StringComparison.Ordinal)) is { Meal: not null } && MentionsEating(text) && context.Nutrition is null)
        {
            replies.Add("Para eu registrar refeições, permita que o assistente leia sua alimentação em Perfil.");
        }
        else if (Meals.FirstOrDefault(m => text.Contains(m.Stem, StringComparison.Ordinal)) is { Meal: not null } meal && MentionsEating(text))
        {
            var planned = context.Nutrition?.TodaysMeals.FirstOrDefault(m => Normalize(m.Name).Contains(meal.Stem, StringComparison.Ordinal));
            var adherence = text.Contains("troquei", StringComparison.Ordinal) || text.Contains(" mas ", StringComparison.Ordinal) || text.Contains("em parte", StringComparison.Ordinal)
                ? "Partly"
                : text.Contains("segui", StringComparison.Ordinal) || text.Contains("conforme", StringComparison.Ordinal) ? "AsPlanned"
                : text.Contains("fora do plano", StringComparison.Ordinal) ? "Unplanned"
                : "Different";
            var share = !(text.Contains("so para mim", StringComparison.Ordinal) || text.Contains("privado", StringComparison.Ordinal));
            calls.Add(Call(AssistantTools.LogMeal, new
            {
                planned_meal_id = planned?.PlannedMealId, meal_name = planned?.Name ?? meal.Meal, adherence, description = FirstClause(request.Message), share_with_dietitian = share,
            }));
            replies.Add("Anotei assim. Confere?");
        }

        if (MentionsPain(text) && context.Training is { } training)
        {
            // "Agora" means the workout in progress; "ontem" the one of yesterday; otherwise the latest finished workout.
            var yesterday = text.Contains("ontem", StringComparison.Ordinal);
            var finished = training.RecentWorkouts.Where(w => w.Status is not ("InProgress" or "Paused")).ToList();
            var target = training.CurrentWorkout is { } current && !yesterday
                ? training.RecentWorkouts.FirstOrDefault(w => w.WorkoutSessionId == current.WorkoutSessionId)
                : yesterday
                    ? finished.FirstOrDefault(w => w.Date == context.Today.AddDays(-1)) ?? finished.FirstOrDefault()
                    : finished.FirstOrDefault();
            if (target is not null)
            {
                calls.Add(Call(AssistantTools.ReportBodyCheck, new
                {
                    workout_session_id = target.WorkoutSessionId, subject = BodyPart(text) ?? "desconforto", answer = "New", details = request.Message.Trim(), report_to_trainer = true,
                }));
                replies.Add($"Sinto muito. Eu não consigo avaliar dor, mas posso avisar {target.Trainer} para ajustar o treino. Quer que eu envie este relato? "
                            + "Se a dor for forte, com inchaço ou o membro falhar, pare e procure atendimento médico.");
            }
        }

        if (calls.Count == 0 && replies.Count == 0)
        {
            replies.Add(Answer(text, context));
        }

        return Task.FromResult<AssistantModelReply?>(new AssistantModelReply(string.Join(" ", replies), calls));
    }

    private static string Answer(string text, AssistantContext context)
    {
        if (text.Contains("quanto", StringComparison.Ordinal) && context.Training is { } training)
        {
            var exercise = training.ExerciseHistory.FirstOrDefault(h => Normalize(h.Name).Split(' ').Any(word => word.Length > 3 && text.Contains(word, StringComparison.Ordinal)));
            return exercise is null
                ? "Não encontrei esse exercício nos seus treinos recentes."
                : $"Em {exercise.Date.ToString("dd/MM", Brazil)}, no {exercise.Name.ToLowerInvariant()}: {string.Join(", ", exercise.Sets)}.";
        }

        if (text.Contains("comer", StringComparison.Ordinal) && context.Nutrition is { } nutrition)
        {
            var next = nutrition.TodaysMeals.FirstOrDefault(m => !m.LoggedToday && string.CompareOrdinal(m.Time ?? "99:99", context.LocalTime) >= 0)
                       ?? nutrition.TodaysMeals.FirstOrDefault(m => !m.LoggedToday);
            return next is null
                ? "Você já registrou todas as refeições de hoje."
                : $"Próxima refeição: {next.Name}{(next.Time is null ? string.Empty : $" às {next.Time}")}: {string.Join(", ", next.Items)}.";
        }

        if (text.Contains("treino", StringComparison.Ordinal) && context.Training is { Plans.Count: > 0 } plans)
        {
            var plan = plans.Plans[0];
            return $"Seu plano {plan.Name}, com {plan.Trainer}, tem {string.Join(", ", plan.Sessions)}.";
        }

        return "Posso registrar refeições, séries e desconfortos e responder sobre os seus planos. O que você quer fazer?";
    }

    private static AssistantToolCall Call(string name, object input) => new(name, JsonSerializer.SerializeToElement(input));

    private static bool LooksLikeSet(string text) =>
        text.Contains("feito", StringComparison.Ordinal) || text.Contains("fiz", StringComparison.Ordinal) || text.Contains("repeti", StringComparison.Ordinal)
        || text.Contains("kg", StringComparison.Ordinal) || text.Contains("serie", StringComparison.Ordinal);

    private static bool MentionsEating(string text) =>
        text.Contains("comi", StringComparison.Ordinal) || text.Contains("almocei", StringComparison.Ordinal) || text.Contains("jantei", StringComparison.Ordinal)
        || text.Contains("tomei", StringComparison.Ordinal) || text.Contains("lanchei", StringComparison.Ordinal) || text.Contains("registrar", StringComparison.Ordinal);

    // Whole words for the short ones: "dois" and "dormi" are not pain.
    private static readonly string[] PainWords = ["dor", "dores", "doi", "doeu", "doendo", "doia"];
    private static readonly string[] PainStems = ["incomod", "desconfort", "machuc", "lesion"];

    private static bool MentionsPain(string text) =>
        Words(text).Any(w => PainWords.Contains(w) || PainStems.Any(stem => w.StartsWith(stem, StringComparison.Ordinal)));

    private static string? BodyPart(string text)
    {
        var words = Words(text);
        var index = Array.FindIndex(words, w => BodyParts.Contains(w));
        if (index < 0)
        {
            return null;
        }

        var side = index + 1 < words.Length && words[index + 1] is "direito" or "esquerdo" or "direita" or "esquerda" ? " " + words[index + 1] : string.Empty;
        var part = words[index] switch { "pescoco" => "pescoço", "pe" => "pé", "mao" => "mão", var p => p };
        return part + side;
    }

    /// <summary>Numbers in digits ("42,5") or words ("quarenta e dois e meio"), in order.</summary>
    private static List<decimal> ParseNumbers(string text)
    {
        var result = new List<decimal>();
        var words = Words(text.Replace(',', '.'));
        decimal? current = null;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (decimal.TryParse(word, NumberStyles.Number, CultureInfo.InvariantCulture, out var digits))
            {
                Flush();
                result.Add(digits);
            }
            else if (Numbers.TryGetValue(word, out var value))
            {
                current = (current ?? 0) + value;
            }
            else if (word == "meio" && current is not null)
            {
                current += 0.5m;
            }
            else if (word != "e" || current is null)
            {
                Flush();
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (current is { } number)
            {
                result.Add(number);
                current = null;
            }
        }
    }

    private static string FirstClause(string message)
    {
        var trimmed = message.Trim();
        foreach (var marker in new[] { " e ontem", " ontem ", ". " })
        {
            var at = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                return trimmed[..at].Trim().TrimEnd('.', ',');
            }
        }

        return trimmed;
    }

    private static string[] Words(string text) =>
        text.Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
