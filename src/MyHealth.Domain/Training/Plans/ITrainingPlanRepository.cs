using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Plans;

public interface ITrainingPlanRepository : IRepository<TrainingPlan, Guid>;

public interface ITrainingPlanVersionRepository : IRepository<TrainingPlanVersion, Guid>;
