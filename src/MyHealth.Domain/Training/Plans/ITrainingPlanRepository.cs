using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Plans;

public interface ITrainingPlanRepository : IRepository<TrainingPlan, Guid>
{
    /// <summary>Draft and published plans of a client, in every tenant.</summary>
    Task<IReadOnlyList<TrainingPlan>> ListOpenForClientAsync(Guid clientId, CancellationToken cancellationToken = default);
}

public interface ITrainingPlanVersionRepository : IRepository<TrainingPlanVersion, Guid>;
