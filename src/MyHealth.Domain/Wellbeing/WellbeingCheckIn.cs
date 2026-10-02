using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Wellbeing;

/// <summary>
/// How the client is today: sleep of the last night and energy now, on 1–5 scales. One per local day; answering again
/// on the same day updates it. The client owns it (no tenant, like the health history); professionals see it only with
/// a <c>Wellbeing</c> sharing grant. It is a self-report, never interpreted.
/// </summary>
public sealed class WellbeingCheckIn : AggregateRoot<Guid>
{
    public const int MinScore = 1;
    public const int MaxScore = 5;

    /// <summary>How far back a day can still be answered.</summary>
    public const int MaxDaysBack = 7;

    private WellbeingCheckIn(Guid id) : base(id) { }

    private WellbeingCheckIn() { }

    public Guid ClientId { get; private set; }

    public DateOnly LocalDate { get; private set; }

    public int? SleepQuality { get; private set; }

    public int? Energy { get; private set; }

    /// <summary>The workout during whose rest it was answered, if any.</summary>
    public Guid? WorkoutSessionId { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<WellbeingCheckIn> Record(
        Guid clientId, DateOnly localDate, DateOnly today, int? sleepQuality, int? energy, Guid? workoutSessionId, DateTimeOffset now)
    {
        var valid = Validate(localDate, today, sleepQuality, energy);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        return new WellbeingCheckIn(Guid.CreateVersion7(now))
        {
            ClientId = clientId,
            LocalDate = localDate,
            SleepQuality = sleepQuality,
            Energy = energy,
            WorkoutSessionId = workoutSessionId,
            RecordedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Answers given again replace the earlier ones; an answer left out keeps its value.</summary>
    public Result Update(DateOnly today, int? sleepQuality, int? energy, Guid? workoutSessionId, DateTimeOffset now)
    {
        var valid = Validate(LocalDate, today, sleepQuality, energy);
        if (valid.IsFailure)
        {
            return valid;
        }

        SleepQuality = sleepQuality ?? SleepQuality;
        Energy = energy ?? Energy;
        WorkoutSessionId = workoutSessionId ?? WorkoutSessionId;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Result Validate(DateOnly localDate, DateOnly today, int? sleepQuality, int? energy)
    {
        if (sleepQuality is null && energy is null)
        {
            return WellbeingErrors.Empty;
        }

        if (sleepQuality is < MinScore or > MaxScore || energy is < MinScore or > MaxScore)
        {
            return WellbeingErrors.OutOfScale;
        }

        return localDate > today || localDate < today.AddDays(-MaxDaysBack) ? WellbeingErrors.DateOutOfRange : Result.Success();
    }
}

public interface IWellbeingCheckInRepository : IRepository<WellbeingCheckIn, Guid>
{
    Task<WellbeingCheckIn?> GetByDateAsync(Guid clientId, DateOnly localDate, CancellationToken cancellationToken = default);
}

public static class WellbeingErrors
{
    public static readonly Error Empty = Error.Validation("Wellbeing.Empty", "Answer at least one question (sleep or energy).");
    public static readonly Error OutOfScale = Error.Validation("Wellbeing.OutOfScale", "Answers go from 1 to 5.");
    public static readonly Error DateOutOfRange = Error.Validation(
        "Wellbeing.DateOutOfRange", $"Check-ins are for today or up to {WellbeingCheckIn.MaxDaysBack} days back.");
}
