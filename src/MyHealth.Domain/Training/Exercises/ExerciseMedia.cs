using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Exercises;

public enum MediaKind
{
    /// <summary>A short, muted loop shown on the workout screen while the client exercises.</summary>
    Loop = 1,

    /// <summary>The full explained video, with chapters and captions.</summary>
    FullVideo = 2,

    Image = 3,

    /// <summary>A 3D model of the movement (glTF binary, <c>.glb</c>), e.g. made from a filmed video by motion capture; the app can rotate it.</summary>
    Model3D = 4,
}

public enum CameraAngle
{
    Side = 1,
    Front = 2,
    Back = 3,
    Other = 4,
}

public sealed record MediaChapter(int AtSeconds, string Title);

/// <summary>
/// One video or image of an exercise, stored on the media host (CDN). The phone downloads the media of the exercises in
/// the client's plan so workouts also work offline.
/// </summary>
public sealed record ExerciseMedia(
    Guid Id,
    MediaKind Kind,
    CameraAngle? Angle,
    string Url,
    string? PosterUrl,
    int? DurationSeconds,
    string? CaptionsUrl,
    IReadOnlyList<MediaChapter> Chapters)
{
    public const int MaxItems = 8;

    public bool IsVideo => Kind is MediaKind.Loop or MediaKind.FullVideo;

    public Result Validate()
    {
        if (Id == Guid.Empty || !IsHttps(Url) || (PosterUrl is not null && !IsHttps(PosterUrl)) || (CaptionsUrl is not null && !IsHttps(CaptionsUrl)))
        {
            return ExerciseErrors.InvalidMedia;
        }

        if (IsVideo && DurationSeconds is not (> 0 and <= 3600))
        {
            return ExerciseErrors.InvalidMedia;
        }

        var previous = -1;
        foreach (var chapter in Chapters ?? [])
        {
            if (Kind != MediaKind.FullVideo || string.IsNullOrWhiteSpace(chapter.Title) || chapter.AtSeconds <= previous || chapter.AtSeconds >= DurationSeconds)
            {
                return ExerciseErrors.InvalidChapters;
            }

            previous = chapter.AtSeconds;
        }

        return Result.Success();
    }

    internal ExerciseMedia Normalized() =>
        this with { Chapters = (Chapters ?? []).Select(c => c with { Title = c.Title.Trim() }).ToList() };

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
