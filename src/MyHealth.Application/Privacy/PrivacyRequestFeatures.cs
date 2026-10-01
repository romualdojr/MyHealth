using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Privacy;

namespace MyHealth.Application.Privacy;

public sealed record PrivacyRequestResponse(
    Guid Id,
    PrivacyRequestType Type,
    PrivacyRequestStatus Status,
    string? Details,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNotes,
    DateTimeOffset? ExpiresAt,
    int DeliveryCount);

/// <summary>The user asks for a correction or a deletion of their data (RNF-04). Platform operators handle it.</summary>
public sealed record SubmitPrivacyRequestCommand(PrivacyRequestType Type, string Details) : ICommand<Guid>;

internal sealed class SubmitPrivacyRequestValidator : AbstractValidator<SubmitPrivacyRequestCommand>
{
    public SubmitPrivacyRequestValidator()
    {
        RuleFor(c => c.Type).IsInEnum();
        RuleFor(c => c.Details).NotEmpty().MaximumLength(4000);
    }
}

internal sealed class SubmitPrivacyRequestHandler(
    IUserContext user, IPrivacyRequestRepository requests, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SubmitPrivacyRequestCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SubmitPrivacyRequestCommand command, CancellationToken cancellationToken)
    {
        var request = PrivacyRequest.Submit(user.UserId, command.Type, command.Details, clock.GetUtcNow());
        if (request.IsFailure)
        {
            return request.Error;
        }

        requests.Add(request.Value);
        audit.Record(AuditActions.Request, $"PrivacyRequest.{command.Type}", request.Value.Id, user.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return request.Value.Id;
    }
}

public sealed record ListMyPrivacyRequestsQuery : IQuery<IReadOnlyList<PrivacyRequestResponse>>;

internal sealed class ListMyPrivacyRequestsHandler(IUserContext user, IReadDbContext db)
    : IQueryHandler<ListMyPrivacyRequestsQuery, IReadOnlyList<PrivacyRequestResponse>>
{
    public async Task<Result<IReadOnlyList<PrivacyRequestResponse>>> Handle(ListMyPrivacyRequestsQuery query, CancellationToken cancellationToken)
    {
        var items = await db.PrivacyRequests
            .Where(r => r.UserId == user.UserId)
            .OrderByDescending(r => r.SubmittedAt)
            .Select(r => new PrivacyRequestResponse(
                r.Id, r.Type, r.Status, r.Details, r.SubmittedAt, r.ResolvedAt, r.ResolutionNotes, r.ExpiresAt, r.DeliveryCount))
            .ToListAsync(cancellationToken);
        return items;
    }
}

public sealed record AdminPrivacyRequestResponse(
    Guid Id,
    Guid UserId,
    string? UserEmail,
    PrivacyRequestType Type,
    PrivacyRequestStatus Status,
    string? Details,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ResolvedAt,
    Guid? HandledBy,
    string? ResolutionNotes,
    int DeliveryCount,
    DateTimeOffset? LastDeliveredAt);

/// <summary>
/// The operators' queue of data-subject requests (RF-50). Exports appear for accountability (requested, delivered) but
/// their contents are never available here.
/// </summary>
public sealed record ListPrivacyRequestsQuery(PrivacyRequestStatus? Status, PrivacyRequestType? Type) : IQuery<IReadOnlyList<AdminPrivacyRequestResponse>>;

internal sealed class ListPrivacyRequestsHandler(IReadDbContext db) : IQueryHandler<ListPrivacyRequestsQuery, IReadOnlyList<AdminPrivacyRequestResponse>>
{
    public async Task<Result<IReadOnlyList<AdminPrivacyRequestResponse>>> Handle(ListPrivacyRequestsQuery query, CancellationToken cancellationToken)
    {
        var requests = db.PrivacyRequests.AsQueryable();
        if (query.Status is { } status)
        {
            requests = requests.Where(r => r.Status == status);
        }

        if (query.Type is { } type)
        {
            requests = requests.Where(r => r.Type == type);
        }

        var items = await (
                from r in requests
                join u in db.UserProfiles on r.UserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby r.SubmittedAt
                select new AdminPrivacyRequestResponse(
                    r.Id, r.UserId, u == null ? null : u.Email, r.Type, r.Status, r.Details, r.SubmittedAt, r.StartedAt, r.ResolvedAt, r.HandledBy,
                    r.ResolutionNotes, r.DeliveryCount, r.LastDeliveredAt))
            .Take(500)
            .ToListAsync(cancellationToken);
        return items;
    }
}

public enum PrivacyRequestAction
{
    Start = 1,
    Complete = 2,
    Reject = 3,
}

/// <summary>An operator moves a correction or deletion request along and records the outcome; the user is notified.</summary>
public sealed record HandlePrivacyRequestCommand(Guid RequestId, PrivacyRequestAction Action, string? Notes) : ICommand;

internal sealed class HandlePrivacyRequestValidator : AbstractValidator<HandlePrivacyRequestCommand>
{
    public HandlePrivacyRequestValidator()
    {
        RuleFor(c => c.Action).IsInEnum();
        RuleFor(c => c.Notes).MaximumLength(4000);
    }
}

internal sealed class HandlePrivacyRequestHandler(
    IUserContext user, IPrivacyRequestRepository requests, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<HandlePrivacyRequestCommand>
{
    public async Task<Result> Handle(HandlePrivacyRequestCommand command, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(command.RequestId, cancellationToken);
        if (request is null)
        {
            return PrivacyErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        var result = command.Action switch
        {
            PrivacyRequestAction.Start => request.Start(user.UserId, now),
            PrivacyRequestAction.Complete => request.Complete(user.UserId, command.Notes, now),
            _ => request.Reject(user.UserId, command.Notes ?? string.Empty, now),
        };

        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Resolve, $"PrivacyRequest.{request.Type}", request.Id, request.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
