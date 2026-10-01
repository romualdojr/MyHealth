using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Workouts;

public interface IWorkoutSessionRepository : IRepository<WorkoutSession, Guid>;
