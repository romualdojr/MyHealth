using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;
using MyHealth.Domain.Wellbeing;

namespace MyHealth.Application.Wellbeing;

public sealed record WellbeingCheckInResponse(
    Guid Id, DateOnly Date, int? SleepQuality, int? Energy, Guid? WorkoutSessionId, DateTimeOffset RecordedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// The client answers "como você está hoje?" for a day (their local date): sleep of the last night and energy now, 1–5.
/// Answering again the same day updates the day's check-in. Often sent during a workout's rest.
/// </summary>
public sealed record SaveWellbeingCheckInCommand(DateOnly Date, int? SleepQuality, int? Energy, Guid? WorkoutSessionId) : ICommand<WellbeingCheckInResponse>;

internal sealed class SaveWellbeingCheckInValidator : AbstractValidator<SaveWellbeingCheckInCommand>
{
    public SaveWellbeingCheckInValidator()
    {
        RuleFor(c => c.SleepQuality).InclusiveBetween(WellbeingCheckIn.MinScore, WellbeingCheckIn.MaxScore);
        RuleFor(c => c.Energy).InclusiveBetween(WellbeingCheckIn.MinScore, WellbeingCheckIn.MaxScore);
        RuleFor(c => c).Must(c => c.SleepQuality is not null || c.Energy is not null).WithMessage("Answer at least one question (sleep or energy).");
    }
}

internal sealed class SaveWellbeingCheckInHandler(
    IUserContext user, IUserProfileRepository users, IWorkoutSessionRepository sessions, IWellbeingCheckInRepository checkIns,
    IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SaveWellbeingCheckInCommand, WellbeingCheckInResponse>
{
    public async Task<Result<WellbeingCheckInResponse>> Handle(SaveWellbeingCheckInCommand command, CancellationToken cancellationToken)
    {
        var profile = await users.GetByIdAsync(user.UserId, cancellationToken);
        if (profile is null)
        {
            return UserErrors.ProfileNotFound;
        }

        if (command.WorkoutSessionId is { } sessionId)
        {
            var session = await sessions.GetByIdAsync(sessionId, cancellationToken);
            if (session is null || session.ClientId != user.UserId)
            {
                return WorkoutErrors.NotFound;
            }
        }

        var now = clock.GetUtcNow();
        var today = TimeZones.LocalDate(now, profile.TimeZoneId);
        var checkIn = await checkIns.GetByDateAsync(user.UserId, command.Date, cancellationToken);
        if (checkIn is null)
        {
            var created = WellbeingCheckIn.Record(user.UserId, command.Date, today, command.SleepQuality, command.Energy, command.WorkoutSessionId, now);
            if (created.IsFailure)
            {
                return created.Error;
            }

            checkIn = created.Value;
            checkIns.Add(checkIn);
        }
        else
        {
            var updated = checkIn.Update(today, command.SleepQuality, command.Energy, command.WorkoutSessionId, now);
            if (updated.IsFailure)
            {
                return updated.Error;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return WellbeingMapping.ToResponse(checkIn);
    }
}

public sealed record ListMyWellbeingQuery(DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<WellbeingCheckInResponse>>;

internal sealed class ListMyWellbeingHandler(IUserContext user, IReadDbContext db) : IQueryHandler<ListMyWellbeingQuery, IReadOnlyList<WellbeingCheckInResponse>>
{
    public async Task<Result<IReadOnlyList<WellbeingCheckInResponse>>> Handle(ListMyWellbeingQuery query, CancellationToken cancellationToken) =>
        Result.Success(await WellbeingMapping.ListAsync(db.WellbeingCheckIns.Where(c => c.ClientId == user.UserId), query.From, query.To, cancellationToken));
}

/// <summary>Check-ins shared with a professional through a <c>Wellbeing</c> grant. Audited.</summary>
public sealed record ListSharedWellbeingQuery(Guid RelationshipId, DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<WellbeingCheckInResponse>>;

internal sealed class ListSharedWellbeingHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListSharedWellbeingQuery, IReadOnlyList<WellbeingCheckInResponse>>
{
    public async Task<Result<IReadOnlyList<WellbeingCheckInResponse>>> Handle(ListSharedWellbeingQuery query, CancellationToken cancellationToken)
    {
        var relationship = await access.GetSharedAccessAsync(query.RelationshipId, DataCategory.Wellbeing, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var r = relationship.Value;
        var items = await WellbeingMapping.ListAsync(db.WellbeingCheckIns.Where(c => c.ClientId == r.ClientId), query.From, query.To, cancellationToken);
        await audit.RecordReadAsync("Wellbeing.List", r.Id, r.ClientId, r.TenantId, cancellationToken);
        return Result.Success(items);
    }
}

internal static class WellbeingMapping
{
    public static WellbeingCheckInResponse ToResponse(WellbeingCheckIn c) =>
        new(c.Id, c.LocalDate, c.SleepQuality, c.Energy, c.WorkoutSessionId, c.RecordedAt, c.UpdatedAt);

    public static async Task<IReadOnlyList<WellbeingCheckInResponse>> ListAsync(
        IQueryable<WellbeingCheckIn> checkIns, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        if (from is { } f)
        {
            checkIns = checkIns.Where(c => c.LocalDate >= f);
        }

        if (to is { } t)
        {
            checkIns = checkIns.Where(c => c.LocalDate <= t);
        }

        return await checkIns
            .OrderByDescending(c => c.LocalDate)
            .Take(366)
            .Select(c => new WellbeingCheckInResponse(c.Id, c.LocalDate, c.SleepQuality, c.Energy, c.WorkoutSessionId, c.RecordedAt, c.UpdatedAt))
            .ToListAsync(cancellationToken);
    }
}
