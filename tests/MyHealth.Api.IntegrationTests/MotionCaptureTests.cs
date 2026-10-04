using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Abstractions.Media;
using MyHealth.Application.Exercises;
using MyHealth.Application.MotionCapture;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.MotionCapture;

namespace MyHealth.Api.IntegrationTests;

/// <summary>3D exercise videos with the fake motion-capture provider: upload, processing by the worker, review and accepting into the exercise.</summary>
public sealed class MotionCaptureTests(ApiFactory factory) : IntegrationTest(factory)
{
    private static readonly byte[] Video = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2', 1, 2, 3, 4];

    private async Task<Guid> OwnExerciseAsync(TestUser trainer, string? mediaCaption = "Agachamento visto de lado, descendo até 90 graus.") =>
        (await Scenario.Read<Scenario.IdResponse>(await trainer.Http.PostAsJsonAsync("/api/v1/exercises", new
        {
            name = $"Agachamento livre {Guid.NewGuid():N}"[..30], description = "Pés na largura dos ombros, descer controlando.", muscleGroups = new[] { "quadríceps" },
            equipment = Array.Empty<string>(), purpose = "força", precautions = (string?)null, mediaCaption,
        }, Ct))).Id;

    private static async Task<HttpResponseMessage> UploadAsync(
        TestUser trainer, Guid exerciseId, int durationSeconds = 8, int consentVersion = MotionCaptureJob.CurrentConsentVersion, string contentType = "video/mp4")
    {
        using var body = new ByteArrayContent(Video);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await trainer.Http.PostAsync(
            $"/api/v1/exercises/{exerciseId}/motion-captures?fileName=agachamento.mp4&durationSeconds={durationSeconds}&consentVersion={consentVersion}", body, Ct);
    }

    private async Task ProcessAsync()
    {
        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<MotionCaptureProcessor>();
        while (await processor.ProcessDueAsync(Ct) > 0)
        {
        }
    }

    [Fact]
    public async Task A_filmed_movement_becomes_a_3d_model_and_rendered_loops_that_the_trainer_puts_on_the_exercise()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var exerciseId = await OwnExerciseAsync(trainer);

        var terms = await Scenario.Read<MotionCaptureTermsResponse>(await trainer.Http.GetAsync("/api/v1/motion-capture/terms", Ct));
        terms.Version.ShouldBe(MotionCaptureJob.CurrentConsentVersion);
        terms.Text.ShouldContain("nunca clientes");

        var uploaded = await UploadAsync(trainer, exerciseId);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created);
        var job = await Scenario.Read<MotionCaptureJobResponse>(uploaded);
        (job.Status, job.DurationSeconds, job.SourceFileName, job.SourceDeleted).ShouldBe((MotionCaptureStatus.Queued, 8, "agachamento.mp4", false));

        // The worker submits the video, then collects the result and deletes the source video.
        await ProcessAsync();
        await ProcessAsync();
        var done = (await Scenario.Read<List<MotionCaptureJobResponse>>(await trainer.Http.GetAsync($"/api/v1/exercises/{exerciseId}/motion-captures", Ct))).ShouldHaveSingleItem();
        (done.Status, done.ProgressPercent, done.SourceDeleted).ShouldBe((MotionCaptureStatus.Succeeded, 100, true));
        var notifications = await Scenario.Read<List<MyHealth.Application.Notifications.NotificationResponse>>(await trainer.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.ShouldContain(n => n.Type == MyHealth.Domain.Notifications.NotificationType.MotionCaptureReady && n.ResourceId == exerciseId);
        done.Outputs.Select(o => (o.Kind, o.Angle, o.Format)).ShouldBe(
        [
            (MediaKind.Loop, CameraAngle.Side, "mp4"), (MediaKind.Loop, CameraAngle.Front, "mp4"), (MediaKind.Loop, CameraAngle.Back, "mp4"), (MediaKind.Model3D, null, "glb"),
        ]);

        // The files are on the media host; the 3D model is a real glTF binary.
        var model = done.Outputs.Single(o => o.Format == "glb");
        var anonymous = Scenario.Factory.CreateClient();
        var path = new Uri(model.Url).AbsolutePath;
        var file = await anonymous.GetAsync(path[path.IndexOf("/media/", StringComparison.Ordinal)..], Ct);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("model/gltf-binary");
        (await file.Content.ReadAsByteArrayAsync(Ct))[..4].ShouldBe("glTF"u8.ToArray());
        (await anonymous.GetAsync("/media/../private/x.mp4", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Accepting puts the model and one loop per angle on the exercise; accepting again changes nothing.
        var accepted = await Scenario.Read<MotionCaptureJobResponse>(await trainer.Http.PostAsync($"/api/v1/motion-captures/{job.Id}/accept", null, Ct));
        accepted.AcceptedAt.ShouldNotBeNull();
        await Scenario.EnsureSuccess(await trainer.Http.PostAsync($"/api/v1/motion-captures/{job.Id}/accept", null, Ct));
        var exercise = await Scenario.Read<ExerciseResponse>(await trainer.Http.GetAsync($"/api/v1/exercises/{exerciseId}", Ct));
        exercise.Media.Select(m => (m.Kind, m.Angle)).ShouldBe([(MediaKind.Loop, CameraAngle.Side), (MediaKind.Loop, CameraAngle.Front), (MediaKind.Loop, CameraAngle.Back), (MediaKind.Model3D, null)]);
        exercise.Media.Where(m => m.Kind == MediaKind.Loop).ShouldAllBe(m => m.DurationSeconds == 8);
    }

    [Fact]
    public async Task Videos_need_consent_a_short_duration_and_an_exercise_of_the_trainer()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var exerciseId = await OwnExerciseAsync(trainer);

        (await UploadAsync(trainer, exerciseId, consentVersion: 0)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await UploadAsync(trainer, exerciseId, durationSeconds: 120)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await UploadAsync(trainer, exerciseId, contentType: "text/plain")).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        // The curated library is maintained by operators; another organization's trainer cannot see the exercise or its jobs.
        var library = await Scenario.AnyLibraryExerciseAsync(trainer);
        (await UploadAsync(trainer, library)).IsSuccessStatusCode.ShouldBeFalse();
        var job = await Scenario.Read<MotionCaptureJobResponse>(await UploadAsync(trainer, exerciseId));
        var (other, _) = await Scenario.ProfessionalAsync("other");
        (await UploadAsync(other, exerciseId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Scenario.Read<List<MotionCaptureJobResponse>>(await other.Http.GetAsync($"/api/v1/exercises/{exerciseId}/motion-captures", Ct))).ShouldBeEmpty();
        (await other.Http.PostAsync($"/api/v1/motion-captures/{job.Id}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Not finished yet: nothing to accept.
        (await trainer.Http.PostAsync($"/api/v1/motion-captures/{job.Id}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Accepting_videos_needs_the_exercise_text_description_first()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var exerciseId = await OwnExerciseAsync(trainer, mediaCaption: null);
        var job = await Scenario.Read<MotionCaptureJobResponse>(await UploadAsync(trainer, exerciseId));
        await ProcessAsync();
        await ProcessAsync();

        var refused = await trainer.Http.PostAsync($"/api/v1/motion-captures/{job.Id}/accept", null, Ct);
        refused.IsSuccessStatusCode.ShouldBeFalse();
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Exercise.");

        // The source video is gone either way.
        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
        var stored = (await Scenario.Read<List<MotionCaptureJobResponse>>(await trainer.Http.GetAsync($"/api/v1/exercises/{exerciseId}/motion-captures", Ct))).Single();
        stored.SourceDeleted.ShouldBeTrue();
        (await storage.OpenAsync($"{IMediaStorage.PublicPrefix}motion/does-not-exist.glb", Ct)).ShouldBeNull();
    }
}
