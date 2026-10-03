using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Privacy;

namespace MyHealth.Application.Privacy;

/// <summary>A record type and its policy, or <c>null</c> fields when none has been set yet (§12.5 is still open).</summary>
public sealed record RetentionPolicyResponse(string RecordType, int? RetentionMonths, string? LegalBasis, string? Notes, DateTimeOffset? UpdatedAt);

public sealed record ListRetentionPoliciesQuery : IQuery<IReadOnlyList<RetentionPolicyResponse>>;

internal sealed class ListRetentionPoliciesHandler(IReadDbContext db) : IQueryHandler<ListRetentionPoliciesQuery, IReadOnlyList<RetentionPolicyResponse>>
{
    public async Task<Result<IReadOnlyList<RetentionPolicyResponse>>> Handle(ListRetentionPoliciesQuery query, CancellationToken cancellationToken)
    {
        var policies = await db.RetentionPolicies.ToDictionaryAsync(p => p.RecordType, cancellationToken);
        return RetentionPolicy.RecordTypes
            .Select(type => policies.TryGetValue(type, out var p)
                ? new RetentionPolicyResponse(type, p.RetentionMonths, p.LegalBasis, p.Notes, p.UpdatedAt)
                : new RetentionPolicyResponse(type, null, null, null, null))
            .ToList();
    }
}

/// <summary>Platform operators record the retention period and legal basis for one record type (RF-50, RNF-01).</summary>
public sealed record SetRetentionPolicyCommand(string RecordType, int RetentionMonths, string LegalBasis, string? Notes) : ICommand;

internal sealed class SetRetentionPolicyValidator : AbstractValidator<SetRetentionPolicyCommand>
{
    public SetRetentionPolicyValidator()
    {
        RuleFor(c => c.RecordType).NotEmpty().MaximumLength(64);
        RuleFor(c => c.LegalBasis).NotEmpty().MaximumLength(1000);
        RuleFor(c => c.Notes).MaximumLength(2000);
    }
}

internal sealed class SetRetentionPolicyHandler(
    IUserContext user, IRetentionPolicyRepository policies, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SetRetentionPolicyCommand>
{
    public async Task<Result> Handle(SetRetentionPolicyCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var policy = await policies.GetByRecordTypeAsync(command.RecordType, cancellationToken);
        if (policy is null)
        {
            var created = RetentionPolicy.Define(command.RecordType, command.RetentionMonths, command.LegalBasis, command.Notes, user.UserId, now);
            if (created.IsFailure)
            {
                return created.Error;
            }

            policy = created.Value;
            policies.Add(policy);
        }
        else
        {
            var updated = policy.Update(command.RetentionMonths, command.LegalBasis, command.Notes, user.UserId, now);
            if (updated.IsFailure)
            {
                return updated;
            }
        }

        audit.Record(AuditActions.Update, $"RetentionPolicy.{command.RecordType}", policy.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
