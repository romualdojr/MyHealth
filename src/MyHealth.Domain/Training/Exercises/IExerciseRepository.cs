using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Exercises;

public interface IExerciseRepository : IRepository<Exercise, Guid>
{
    Task<IReadOnlyList<Exercise>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
}
