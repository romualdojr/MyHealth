using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Assessments;

/// <summary>A Monday-to-Sunday week, clipped to the requested period. Zero sessions is a fact, not missing data.</summary>
public sealed record ProgressWeek(DateOnly From, DateOnly To, int CompletedSessions, int ActiveDays);

/// <summary>Where a measured value came from, so every chart point shows its source and date (RF-20, RF-24).</summary>
public sealed record MeasurementPoint(DateOnly Date, decimal Value, Guid AssessmentId, string Source, string OrganizationName, string AssessorName);

/// <summary>
/// One comparable series: same metric, method and unit. Values measured with another method form another series and are
/// never merged. Only recorded points are returned: nothing is interpolated between them.
/// </summary>
public sealed record MeasurementSeries(string Metric, string Method, string Unit, IReadOnlyList<MeasurementPoint> Points);

/// <summary>Self-reported load progression for one exercise in the period; null where no load was logged.</summary>
public sealed record ExerciseProgressSummary(
    Guid ExerciseId, string Name, int Sessions, DateOnly FirstDate, DateOnly LastDate, decimal? FirstMaxLoadKg, decimal? LastMaxLoadKg, decimal? BestLoadKg);

/// <summary>A goal set by a professional, labelled by where it was defined.</summary>
public sealed record ProfessionalGoal(string Source, string Description, Guid TenantId, string OrganizationName, string ProfessionalName, DateOnly Since);

public sealed record MyProgressResponse(
    DateOnly From,
    DateOnly To,
    int CompletedSessions,
    int ActiveDays,
    IReadOnlyList<ProgressWeek> Weeks,
    IReadOnlyList<MeasurementSeries> Measurements,
    IReadOnlyList<ExerciseProgressSummary> Exercises,
    IReadOnlyList<ProfessionalGoal> Goals);

/// <summary>
/// My Progress (RF-25): attendance, assessments, exercise progression and professionally defined goals for a period,
/// across every organization (MT-10). Defaults to the last 12 weeks.
/// </summary>
public sealed record GetMyProgressQuery(DateOnly? From, DateOnly? To) : IQuery<MyProgressResponse>;

internal sealed class GetMyProgressValidator : AbstractValidator<GetMyProgressQuery>
{
    public GetMyProgressValidator() =>
        RuleFor(q => q.To)
            .Must((q, to) => q.From is null || to is null || (to >= q.From && to.Value.DayNumber - q.From.Value.DayNumber <= 366))
            .WithMessage("The period must end after it starts and cannot exceed one year.");
}

internal sealed class GetMyProgressHandler(IUserContext user, IReadDbContext db, TimeProvider clock) : IQueryHandler<GetMyProgressQuery, MyProgressResponse>
{
    public const string PhysicalAssessmentSource = "physical-assessment";
    public const string NutritionAssessmentSource = "nutrition-assessment";

    public async Task<Result<MyProgressResponse>> Handle(GetMyProgressQuery query, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == user.UserId, cancellationToken);
        if (profile is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var to = query.To ?? TimeZones.LocalDate(clock.GetUtcNow(), profile.TimeZoneId);
        var from = query.From ?? to.AddDays(-83);
        if (from > to || to.DayNumber - from.DayNumber > 366)
        {
            return Error.Validation("Progress.InvalidPeriod", "The period must end after it starts and cannot exceed one year.");
        }

        var sessions = await db.WorkoutSessions
            .Where(s => s.ClientId == user.UserId && s.Status == WorkoutStatus.Completed && s.LocalDate >= from && s.LocalDate <= to)
            .Select(s => new { s.Id, LocalDate = s.LocalDate!.Value, Sets = s.Sets.Select(set => new { set.ExerciseId, set.LoadKg }).ToList() })
            .ToListAsync(cancellationToken);

        var weeks = Weeks(from, to)
            .Select(w =>
            {
                var inWeek = sessions.Where(s => s.LocalDate >= w.From && s.LocalDate <= w.To).ToList();
                return new ProgressWeek(w.From, w.To, inWeek.Count, inWeek.Select(s => s.LocalDate).Distinct().Count());
            })
            .ToList();

        var exerciseIds = sessions.SelectMany(s => s.Sets.Select(set => set.ExerciseId)).Distinct().ToList();
        var names = await db.Exercises.Where(e => exerciseIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken);
        var exercises = exerciseIds
            .Select(id =>
            {
                var performed = sessions
                    .Where(s => s.Sets.Any(set => set.ExerciseId == id))
                    .OrderBy(s => s.LocalDate)
                    .Select(s => new { s.LocalDate, MaxLoad = s.Sets.Where(set => set.ExerciseId == id).Max(set => set.LoadKg) })
                    .ToList();
                return new ExerciseProgressSummary(
                    id, names.GetValueOrDefault(id, string.Empty), performed.Count, performed[0].LocalDate, performed[^1].LocalDate,
                    performed[0].MaxLoad, performed[^1].MaxLoad, performed.Max(p => p.MaxLoad));
            })
            .OrderBy(e => e.Name)
            .ToList();

        return new MyProgressResponse(
            from, to, sessions.Count, sessions.Select(s => s.LocalDate).Distinct().Count(), weeks,
            await MeasurementsAsync(from, to, cancellationToken), exercises, await GoalsAsync(cancellationToken));
    }

    private async Task<IReadOnlyList<MeasurementSeries>> MeasurementsAsync(DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken)
    {
        var physical = await (
                from a in db.PhysicalAssessments
                join t in db.Tenants on a.TenantId equals t.Id
                join p in db.ProfessionalProfiles on a.AssessorProfileId equals p.Id
                where a.ClientId == user.UserId && a.AssessedOn >= periodStart && a.AssessedOn <= periodEnd
                select new { a.Id, a.AssessedOn, a.Measurements, Organization = t.Name, Assessor = p.DisplayName })
            .ToListAsync(cancellationToken);

        var nutrition = await (
                from a in db.NutritionAssessments
                join t in db.Tenants on a.TenantId equals t.Id
                join p in db.ProfessionalProfiles on a.DietitianProfileId equals p.Id
                where a.ClientId == user.UserId && a.AssessedOn >= periodStart && a.AssessedOn <= periodEnd
                select new { a.Id, a.AssessedOn, a.Content, Organization = t.Name, Assessor = p.DisplayName })
            .ToListAsync(cancellationToken);

        var points = physical
            .SelectMany(a => a.Measurements.Select(m => (m, new MeasurementPoint(a.AssessedOn, m.Value, a.Id, PhysicalAssessmentSource, a.Organization, a.Assessor))))
            .Concat(nutrition.SelectMany(a => a.Content.Measurements.Select(m =>
                (m, new MeasurementPoint(a.AssessedOn, m.Value, a.Id, NutritionAssessmentSource, a.Organization, a.Assessor)))));

        return points
            .GroupBy(x => (x.m.Metric, x.m.Method, x.m.Unit))
            .Select(g => new MeasurementSeries(g.Key.Metric, g.Key.Method, g.Key.Unit, g.Select(x => x.Item2).OrderBy(p => p.Date).ToList()))
            .OrderBy(s => s.Metric)
            .ThenBy(s => s.Method)
            .ToList();
    }

    private async Task<IReadOnlyList<ProfessionalGoal>> GoalsAsync(CancellationToken cancellationToken)
    {
        var training = await (
                from plan in db.TrainingPlans
                join version in db.TrainingPlanVersions on plan.CurrentVersionId equals version.Id
                join t in db.Tenants on plan.TenantId equals t.Id
                join p in db.ProfessionalProfiles on plan.TrainerProfileId equals p.Id
                where plan.ClientId == user.UserId && plan.Status == PlanStatus.Published
                select new ProfessionalGoal("training-plan", version.Details.Goal, plan.TenantId, t.Name, p.DisplayName, version.Details.StartDate))
            .ToListAsync(cancellationToken);

        var meals = await (
                from plan in db.MealPlans
                join version in db.MealPlanVersions on plan.CurrentVersionId equals version.Id
                join t in db.Tenants on plan.TenantId equals t.Id
                join p in db.ProfessionalProfiles on plan.DietitianProfileId equals p.Id
                where plan.ClientId == user.UserId && plan.Status == MealPlanStatus.Published && version.Details.Goal != null
                select new ProfessionalGoal("meal-plan", version.Details.Goal!, plan.TenantId, t.Name, p.DisplayName, version.Details.StartDate))
            .ToListAsync(cancellationToken);

        // The most recent nutrition assessment with goals, per organization.
        var assessments = await (
                from a in db.NutritionAssessments
                join t in db.Tenants on a.TenantId equals t.Id
                join p in db.ProfessionalProfiles on a.DietitianProfileId equals p.Id
                where a.ClientId == user.UserId
                select new { a.TenantId, a.AssessedOn, a.Content, Organization = t.Name, Dietitian = p.DisplayName })
            .ToListAsync(cancellationToken);
        var nutrition = assessments
            .Where(a => !string.IsNullOrWhiteSpace(a.Content.Goals))
            .GroupBy(a => a.TenantId)
            .Select(g => g.MaxBy(a => a.AssessedOn)!)
            .Select(a => new ProfessionalGoal(NutritionAssessmentSource, a.Content.Goals!, a.TenantId, a.Organization, a.Dietitian, a.AssessedOn));

        return training.Concat(meals).Concat(nutrition).OrderByDescending(g => g.Since).ToList();
    }

    private static IEnumerable<(DateOnly From, DateOnly To)> Weeks(DateOnly from, DateOnly to)
    {
        var start = from;
        while (start <= to)
        {
            var daysToSunday = ((int)DayOfWeek.Sunday - (int)start.DayOfWeek + 7) % 7;
            var end = start.AddDays(daysToSunday);
            yield return (start, end > to ? to : end);
            start = end.AddDays(1);
        }
    }
}
