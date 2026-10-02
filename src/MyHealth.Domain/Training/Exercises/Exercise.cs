using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Exercises;

/// <summary>
/// A library entry. System entries have no tenant and no owner and are visible everywhere; professionals may add their
/// own, which belong to the tenant they were created in and are visible only to it. Every change bumps
/// <see cref="Version"/>, and plans keep the version they were written against.
/// </summary>
public sealed class Exercise : AggregateRoot<Guid>
{
    private Exercise(Guid id, Guid? tenantId, Guid? ownerProfileId, DateTimeOffset createdAt) : base(id)
    {
        TenantId = tenantId;
        OwnerProfileId = ownerProfileId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    private Exercise() { }

    /// <summary>The tenant whose library contains it, or <c>null</c> for curated system entries.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>The professional profile that created it, or <c>null</c> for curated system entries.</summary>
    public Guid? OwnerProfileId { get; private set; }

    public string Name { get; private set; } = null!;

    public string Description { get; private set; } = null!;

    public List<string> MuscleGroups { get; private set; } = [];

    public List<string> Equipment { get; private set; } = [];

    public string? Purpose { get; private set; }

    public string? Precautions { get; private set; }

    /// <summary>Text description of the movement for people who cannot watch the video (accessibility). Required once the exercise has video.</summary>
    public string? MediaCaption { get; private set; }

    /// <summary>
    /// Videos and images of the movement, owned or licensed only. Changing them does not change the prescription, so it
    /// does not bump <see cref="Version"/>; clients always get the current media.
    /// </summary>
    public List<ExerciseMedia> Media { get; private set; } = [];

    public int Version { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A curated entry available to every tenant.</summary>
    public static Exercise CreateSystem(ExerciseDetails details, DateTimeOffset now) => Build(null, null, details, now);

    /// <summary>A professional's own entry, part of the given tenant's library.</summary>
    public static Exercise CreateCustom(Guid tenantId, Guid ownerProfileId, ExerciseDetails details, DateTimeOffset now) =>
        Build(tenantId, ownerProfileId, details, now);

    /// <summary>System entries, plus entries of the given tenant.</summary>
    public bool IsAvailableIn(Guid? tenantId) => TenantId is null || TenantId == tenantId;

    private static Exercise Build(Guid? tenantId, Guid? ownerProfileId, ExerciseDetails details, DateTimeOffset now)
    {
        var exercise = new Exercise(Guid.CreateVersion7(now), tenantId, ownerProfileId, now);
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

    public bool IsSystem => TenantId is null && OwnerProfileId is null;

    /// <summary>Platform operators maintain the curated library (RF-50). Plans keep the version they were written against.</summary>
    public Result UpdateSystem(ExerciseDetails details, DateTimeOffset now)
    {
        if (!IsSystem)
        {
            return ExerciseErrors.NotSystem;
        }

        Apply(details);
        Version++;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Hides a curated entry from new prescriptions. Existing plans are unaffected.</summary>
    public Result SetArchived(bool archived, DateTimeOffset now)
    {
        if (!IsSystem)
        {
            return ExerciseErrors.NotSystem;
        }

        IsArchived = archived;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>The creator replaces the media of their own exercise.</summary>
    public Result SetMedia(Guid profileId, IReadOnlyList<ExerciseMedia> media, DateTimeOffset now) =>
        CanBeEditedBy(profileId) ? ReplaceMedia(media, now) : ExerciseErrors.NotOwner;

    /// <summary>Platform operators maintain the media of the curated library.</summary>
    public Result SetSystemMedia(IReadOnlyList<ExerciseMedia> media, DateTimeOffset now) =>
        IsSystem ? ReplaceMedia(media, now) : ExerciseErrors.NotSystem;

    private Result ReplaceMedia(IReadOnlyList<ExerciseMedia> media, DateTimeOffset now)
    {
        if (media.Count > ExerciseMedia.MaxItems)
        {
            return ExerciseErrors.TooManyMedia;
        }

        if (media.Select(m => m.Id).Distinct().Count() != media.Count)
        {
            return ExerciseErrors.DuplicateMedia;
        }

        // One loop per camera angle: the workout screen picks it without asking.
        if (media.Where(m => m.Kind == MediaKind.Loop).GroupBy(m => m.Angle).Any(g => g.Count() > 1))
        {
            return ExerciseErrors.DuplicateLoop;
        }

        foreach (var item in media)
        {
            var valid = item.Validate();
            if (valid.IsFailure)
            {
                return valid;
            }
        }

        if (media.Any(m => m.IsVideo) && string.IsNullOrWhiteSpace(MediaCaption))
        {
            return ExerciseErrors.MediaDescriptionRequired;
        }

        Media = media.Select(m => m.Normalized()).ToList();
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
    string? MediaCaption);

public static class ExerciseErrors
{
    public static readonly Error NotFound = Error.NotFound("Exercise.NotFound", "The exercise was not found.");
    public static readonly Error NotOwner = Error.Forbidden("Exercise.NotOwner", "Only the professional who created this exercise can change it.");
    public static readonly Error NotSystem = Error.Validation("Exercise.NotSystem", "Only curated library entries are managed by platform operators.");
    public static readonly Error TooManyMedia = Error.Validation("Exercise.TooManyMedia", $"An exercise has at most {ExerciseMedia.MaxItems} media items.");
    public static readonly Error DuplicateMedia = Error.Validation("Exercise.DuplicateMedia", "Media ids must be unique.");
    public static readonly Error DuplicateLoop = Error.Validation("Exercise.DuplicateLoop", "Only one loop per camera angle.");
    public static readonly Error MediaDescriptionRequired = Error.Validation(
        "Exercise.MediaDescriptionRequired", "Add a text description of the movement (mediaCaption) before adding video, for people who cannot watch it.");
    public static readonly Error InvalidMedia = Error.Validation("Exercise.InvalidMedia", "Each media item needs an https URL, and a video needs its duration.");
    public static readonly Error InvalidChapters = Error.Validation(
        "Exercise.InvalidChapters", "Chapters need a title and must be in order, inside the video's duration.");

    public static Error Unknown(IEnumerable<Guid> ids) =>
        Error.Validation("Exercise.Unknown", $"Unknown or unavailable exercises: {string.Join(", ", ids)}.");
}
