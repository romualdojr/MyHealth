using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Assistant;

/// <summary>What the assistant prepared for the client to confirm. Each kind maps to an existing command.</summary>
public enum ProposalKind
{
    FoodDiaryEntry = 1,
    BodyCheck = 2,
    SetLog = 3,
}

public enum ProposalStatus
{
    Pending = 1,
    Confirmed = 2,
    Discarded = 3,
}

/// <summary>
/// A record the assistant prepared from what the client said. Nothing is saved until the client confirms; confirming
/// runs the same command the app's forms use, as the client, so the same rules, sharing and audit apply. The payload
/// is the command's input (jsonb) and carries client-style ids, so confirming twice never creates two records.
/// </summary>
public sealed class AssistantProposal : AggregateRoot<Guid>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private AssistantProposal(Guid id) : base(id) { }

    private AssistantProposal() { }

    public Guid UserId { get; private set; }

    public ProposalKind Kind { get; private set; }

    public string Payload { get; private set; } = null!;

    public ProposalStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public static AssistantProposal Create(Guid userId, ProposalKind kind, string payload, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            UserId = userId,
            Kind = kind,
            Payload = payload,
            Status = ProposalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };

    public bool IsOpen(DateTimeOffset now) => Status == ProposalStatus.Pending && now < ExpiresAt;

    public Result Confirm(DateTimeOffset now)
    {
        if (Status == ProposalStatus.Confirmed)
        {
            return Result.Success();
        }

        if (!IsOpen(now))
        {
            return AssistantErrors.ProposalClosed;
        }

        Status = ProposalStatus.Confirmed;
        ResolvedAt = now;
        return Result.Success();
    }

    public Result Discard(DateTimeOffset now)
    {
        if (Status == ProposalStatus.Discarded)
        {
            return Result.Success();
        }

        if (Status == ProposalStatus.Confirmed)
        {
            return AssistantErrors.ProposalClosed;
        }

        Status = ProposalStatus.Discarded;
        ResolvedAt = now;
        return Result.Success();
    }
}

public interface IAssistantProposalRepository : IRepository<AssistantProposal, Guid>
{
    /// <summary>Removes the user's suggestions older than a day, confirmed or not; the records they created remain.</summary>
    Task DeleteStaleAsync(Guid userId, DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}
