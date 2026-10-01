using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Plans;

/// <summary>An immutable published snapshot of a plan. Workout sessions always reference exactly one version.</summary>
public sealed class TrainingPlanVersion : AggregateRoot<Guid>
{
    private TrainingPlanVersion(Guid id) : base(id) { }

    private TrainingPlanVersion() { }

    public Guid TrainingPlanId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid TrainerProfileId { get; private set; }

    public Guid TrainerUserId { get; private set; }

    public int VersionNumber { get; private set; }

    public PlanDetails Details { get; private set; } = null!;

    public PlanContent Content { get; private set; } = PlanContent.Empty;

    public string ChangeDescription { get; private set; } = null!;

    public DateTimeOffset PublishedAt { get; private set; }

    internal static TrainingPlanVersion Create(TrainingPlan plan, int versionNumber, string changeDescription, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            TrainingPlanId = plan.Id,
            ClientId = plan.ClientId,
            TrainerProfileId = plan.TrainerProfileId,
            TrainerUserId = plan.TrainerUserId,
            VersionNumber = versionNumber,
            Details = plan.Details,
            Content = plan.Draft,
            ChangeDescription = changeDescription.Trim(),
            PublishedAt = now,
        };
}
