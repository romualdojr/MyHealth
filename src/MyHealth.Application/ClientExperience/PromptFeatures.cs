using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Therapies;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Application.ClientExperience;

public enum PromptKind
{
    /// <summary>Follow-up of a discomfort the client reported to the trainer.</summary>
    BodyCheck = 1,

    /// <summary>A planned meal whose time has passed and is not in the diary yet.</summary>
    Meal = 2,

    /// <summary>Sleep and energy, once a day.</summary>
    Wellbeing = 3,

    /// <summary>How the client felt after a completed therapy session.</summary>
    TherapyFeedback = 4,
}

public sealed record PromptOption(string Value, string Label);

/// <summary>One answer to collect; <see cref="Field"/> is the request field it goes to.</summary>
public sealed record PromptQuestion(string Field, string Text, IReadOnlyList<PromptOption> Options, string? LowLabel = null, string? HighLabel = null);

/// <summary>Who receives the answer. The card shows it, so the client always knows (MT-10).</summary>
public sealed record PromptRecipient(string Name, string OrganizationName);

/// <summary>The ids the answer needs. Only the ones relevant to the prompt's kind are set.</summary>
public sealed record PromptTarget(
    Guid? WorkoutSessionId = null,
    string? Subject = null,
    Guid? MealPlanId = null,
    Guid? PlannedMealId = null,
    string? MealName = null,
    Guid? TherapySessionId = null,
    DateOnly? Date = null);

/// <summary>
/// A small question to ask when the client has a moment, typically during a workout's rest. It is answered with the
/// existing endpoint named in <see cref="AnswerWith"/>, so the same rules and sharing apply as in the forms.
/// </summary>
public sealed record PromptItem(
    string Key,
    PromptKind Kind,
    string Title,
    string? Context,
    IReadOnlyList<PromptQuestion> Questions,
    IReadOnlyList<PromptRecipient> Recipients,
    string? RecipientNote,
    PromptTarget Target,
    string AnswerWith);

/// <summary>
/// The day's pending questions for the client, most important first and at most <see cref="Max"/>: a follow-up of a
/// reported discomfort (only with a workout in progress), a past meal not yet in the diary, the daily wellbeing
/// check-in, and feedback on a recent therapy session. Built from existing records, so answering one removes it. The
/// app decides when to show them (one per rest, never in the last seconds); skipping is local to the phone.
/// </summary>
public sealed record GetMyPromptsQuery(Guid? WorkoutSessionId) : IQuery<IReadOnlyList<PromptItem>>
{
    public const int Max = 4;
}

internal sealed class GetMyPromptsHandler(IUserContext user, IReadDbContext db, TimeProvider clock) : IQueryHandler<GetMyPromptsQuery, IReadOnlyList<PromptItem>>
{
    private static readonly TimeSpan MealWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan DiscomfortLookBack = TimeSpan.FromDays(14);
    private static readonly TimeSpan TherapyLookBack = TimeSpan.FromDays(7);

    private static readonly PromptOption[] FivePoints = [.. Enumerable.Range(1, 5).Select(i => new PromptOption(i.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture)))];

    public async Task<Result<IReadOnlyList<PromptItem>>> Handle(GetMyPromptsQuery query, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == user.UserId, cancellationToken);
        if (profile is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var now = clock.GetUtcNow();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);

        var prompts = new List<PromptItem>();
        if (query.WorkoutSessionId is { } sessionId)
        {
            var current = await db.WorkoutSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.ClientId == user.UserId, cancellationToken);
            if (current is null)
            {
                return WorkoutErrors.NotFound;
            }

            if (await BodyCheckAsync(current, now, cancellationToken) is { } bodyCheck)
            {
                prompts.Add(bodyCheck);
            }
        }

        if (await MealAsync(today, TimeOnly.FromDateTime(localNow.DateTime), cancellationToken) is { } meal)
        {
            prompts.Add(meal);
        }

        if (await WellbeingAsync(today, cancellationToken) is { } wellbeing)
        {
            prompts.Add(wellbeing);
        }

        if (await TherapyAsync(now, zone, cancellationToken) is { } therapy)
        {
            prompts.Add(therapy);
        }

        return prompts.Take(GetMyPromptsQuery.Max).ToList();
    }

    /// <summary>The discomfort reported to this trainer in a recent workout, asked again once per workout.</summary>
    private async Task<PromptItem?> BodyCheckAsync(WorkoutSession current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!current.IsOpen || current.BodyChecks.Count > 0)
        {
            return null;
        }

        var since = now - DiscomfortLookBack;
        var previous = await db.WorkoutSessions
            .Where(s => s.ClientId == user.UserId && s.TrainerProfileId == current.TrainerProfileId && s.Id != current.Id
                        && (s.Status == WorkoutStatus.Completed || s.Status == WorkoutStatus.Abandoned) && s.StartedAt >= since)
            .OrderByDescending(s => s.StartedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        var source = previous.Select(s => (Session: s, Discomfort: s.LatestReportedDiscomfort())).FirstOrDefault(x => x.Discomfort is not null);
        if (source.Discomfort is not { } discomfort)
        {
            return null;
        }

        var trainer = await RecipientAsync(current.TrainerProfileId, current.TenantId, cancellationToken);
        var subject = discomfort.Subject ?? "o desconforto relatado";
        var title = discomfort.Subject is null ? "No último treino você relatou desconforto. Como está agora?" : $"Como está {discomfort.Subject} nesta série?";
        var context = discomfort.Details is null ? null : $"Em {source.Session.StartedAt:dd/MM} você disse: \"{discomfort.Details}\"";

        return new PromptItem(
            $"body-check:{current.Id}",
            PromptKind.BodyCheck,
            title,
            context,
            [new PromptQuestion("answer", title, [new("Better", "Melhor"), new("Same", "Igual"), new("Worse", "Pior")])],
            trainer is null ? [] : [trainer],
            "Se piorar, você escolhe se avisa o treinador agora. Se a dor for forte, com inchaço ou o membro falhar, pare e procure atendimento médico.",
            new PromptTarget(WorkoutSessionId: current.Id, Subject: subject),
            $"PUT /api/v1/workout-sessions/{current.Id}/body-checks/{{checkId}}");
    }

    /// <summary>The latest planned meal of the last hours that is not in today's diary.</summary>
    private async Task<PromptItem?> MealAsync(DateOnly today, TimeOnly localTime, CancellationToken cancellationToken)
    {
        var plans = await (
                from plan in db.MealPlans
                join version in db.MealPlanVersions on plan.CurrentVersionId equals version.Id
                join dietitian in db.ProfessionalProfiles on plan.DietitianProfileId equals dietitian.Id
                join tenant in db.Tenants on plan.TenantId equals tenant.Id
                where plan.ClientId == user.UserId && plan.Status == MealPlanStatus.Published
                select new { plan.Id, version.Content, Dietitian = dietitian.DisplayName, Organization = tenant.Name })
            .ToListAsync(cancellationToken);

        var earliest = localTime.Add(-MealWindow, out var wrappedDays);
        var wrapped = wrappedDays != 0;
        var due = plans
            .SelectMany(p => p.Content.Meals.Select(m => (Plan: p, Meal: m)))
            .Where(x => x.Meal.Time is { } time && time <= localTime && (wrapped || time >= earliest))
            .OrderByDescending(x => x.Meal.Time)
            .ToList();
        if (due.Count == 0)
        {
            return null;
        }

        var mealIds = due.Select(x => (Guid?)x.Meal.Id).ToList();
        var logged = await db.FoodDiaryEntries
            .Where(e => e.ClientId == user.UserId && e.LocalDate == today && mealIds.Contains(e.PlannedMealId))
            .Select(e => e.PlannedMealId)
            .ToListAsync(cancellationToken);

        var next = due.FirstOrDefault(x => !logged.Contains(x.Meal.Id));
        if (next.Plan is null)
        {
            return null;
        }

        var title = $"{next.Meal.Name} de hoje: como foi?";
        return new PromptItem(
            $"meal:{today:yyyy-MM-dd}:{next.Meal.Id}",
            PromptKind.Meal,
            title,
            null,
            [
                new PromptQuestion(
                    "adherence", title,
                    [new(nameof(MealAdherence.AsPlanned), "Segui o plano"), new(nameof(MealAdherence.Partly), "Segui em parte"), new(nameof(MealAdherence.Different), "Comi outra coisa")]),
            ],
            [new PromptRecipient(next.Plan.Dietitian, next.Plan.Organization)],
            "Seu treinador não vê o diário alimentar.",
            new PromptTarget(MealPlanId: next.Plan.Id, PlannedMealId: next.Meal.Id, MealName: next.Meal.Name),
            "PUT /api/v1/me/food-diary/{entryId}");
    }

    private async Task<PromptItem?> WellbeingAsync(DateOnly today, CancellationToken cancellationToken)
    {
        if (await db.WellbeingCheckIns.AnyAsync(c => c.ClientId == user.UserId && c.LocalDate == today, cancellationToken))
        {
            return null;
        }

        var recipients = await (
                from r in db.CareRelationships
                from g in r.Grants
                join p in db.ProfessionalProfiles on r.ProfessionalProfileId equals p.Id
                join t in db.Tenants on r.TenantId equals t.Id
                where r.ClientId == user.UserId && r.Status == RelationshipStatus.Active && g.Category == DataCategory.Wellbeing && g.RevokedAt == null
                select new PromptRecipient(p.DisplayName, t.Name))
            .ToListAsync(cancellationToken);
        recipients = recipients.Distinct().ToList();

        return new PromptItem(
            $"wellbeing:{today:yyyy-MM-dd}",
            PromptKind.Wellbeing,
            "Como você está hoje?",
            null,
            [
                new PromptQuestion("sleepQuality", "Sono da última noite", FivePoints, "ruim", "ótimo"),
                new PromptQuestion("energy", "Energia agora", FivePoints, "sem energia", "cheia"),
            ],
            recipients,
            recipients.Count == 0 ? "Fica só com você: nenhum profissional tem acesso." : null,
            new PromptTarget(Date: today),
            $"PUT /api/v1/me/wellbeing/{today:yyyy-MM-dd}");
    }

    private async Task<PromptItem?> TherapyAsync(DateTimeOffset now, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var since = now - TherapyLookBack;
        var session = await (
                from s in db.TherapySessions
                join m in db.TherapyModalities on s.ModalityId equals m.Id
                join p in db.ProfessionalProfiles on s.PractitionerProfileId equals p.Id
                join t in db.Tenants on s.TenantId equals t.Id
                where s.ClientId == user.UserId && s.Status == TherapySessionStatus.Completed && s.ClientReport == null && s.CompletedAt >= since
                orderby s.CompletedAt descending
                select new { s.Id, s.StartsAt, Modality = m.Name, Practitioner = p.DisplayName, Organization = t.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return null;
        }

        var date = TimeZoneInfo.ConvertTime(session.StartsAt, zone);
        var title = $"Como você ficou depois da sessão de {session.Modality.ToLowerInvariant()} de {date:dd/MM}?";
        return new PromptItem(
            $"therapy:{session.Id}",
            PromptKind.TherapyFeedback,
            title,
            null,
            [new PromptQuestion("wellbeing", title, FivePoints, "pior", "muito melhor")],
            [new PromptRecipient(session.Practitioner, session.Organization)],
            null,
            new PromptTarget(TherapySessionId: session.Id),
            $"POST /api/v1/me/therapy-sessions/{session.Id}/progress");
    }

    private Task<PromptRecipient?> RecipientAsync(Guid profileId, Guid tenantId, CancellationToken cancellationToken) =>
        (from p in db.ProfessionalProfiles
         from t in db.Tenants
         where p.Id == profileId && t.Id == tenantId
         select new PromptRecipient(p.DisplayName, t.Name))
        .FirstOrDefaultAsync(cancellationToken);
}
