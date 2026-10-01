using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Exercises;

/// <summary>
/// A library entry. System entries have no owner; professionals may add their own. Every change bumps
/// <see cref="Version"/>, and plans keep the version they were written against.
/// </summary>
public sealed class Exercise : AggregateRoot<Guid>
{
    private Exercise(Guid id, Guid? ownerProfileId, DateTimeOffset createdAt) : base(id)
    {
        OwnerProfileId = ownerProfileId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    private Exercise() { }

    /// <summary>The professional profile that created it, or <c>null</c> for curated system entries.</summary>
    public Guid? OwnerProfileId { get; private set; }

    public string Name { get; private set; } = null!;

    public string Description { get; private set; } = null!;

    public List<string> MuscleGroups { get; private set; } = [];

    public List<string> Equipment { get; private set; } = [];

    public string? Purpose { get; private set; }

    public string? Precautions { get; private set; }

    /// <summary>Owned or licensed media only.</summary>
    public string? MediaUrl { get; private set; }

    /// <summary>Instructional text or captions for the media (accessibility).</summary>
    public string? MediaCaption { get; private set; }

    public int Version { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Exercise Create(Guid? ownerProfileId, ExerciseDetails details, DateTimeOffset now)
    {
        var exercise = new Exercise(Guid.CreateVersion7(now), ownerProfileId, now);
        exercise.Apply(details);
        return exercise;
    }

    public bool CanBeEditedBy(Guid? profileId) => OwnerProfileId is not null && OwnerProfileId == profileId;

    public Result Update(Guid profileId, ExerciseDetails details, DateTimeOffset now)
    {
        if (!CanBeEditedBy(profileId))
        {
            return ExerciseErrors.NotOwner;
        }

        Apply(details);
        Version++;
        UpdatedAt = now;
        return Result.Success();
    }

    private void Apply(ExerciseDetails details)
    {
        Name = details.Name.Trim();
        Description = details.Description.Trim();
        MuscleGroups = Normalize(details.MuscleGroups);
        Equipment = Normalize(details.Equipment);
        Purpose = details.Purpose;
        Precautions = details.Precautions;
        MediaUrl = details.MediaUrl;
        MediaCaption = details.MediaCaption;
    }

    private static List<string> Normalize(IEnumerable<string> values) =>
        values.Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0).Distinct().ToList();
}

public sealed record ExerciseDetails(
    string Name,
    string Description,
    IReadOnlyCollection<string> MuscleGroups,
    IReadOnlyCollection<string> Equipment,
    string? Purpose,
    string? Precautions,
    string? MediaUrl,
    string? MediaCaption);

public static class ExerciseErrors
{
    public static readonly Error NotFound = Error.NotFound("Exercise.NotFound", "The exercise was not found.");
    public static readonly Error NotOwner = Error.Forbidden("Exercise.NotOwner", "Only the professional who created this exercise can change it.");

    public static Error Unknown(IEnumerable<Guid> ids) =>
        Error.Validation("Exercise.Unknown", $"Unknown or unavailable exercises: {string.Join(", ", ids)}.");
}
