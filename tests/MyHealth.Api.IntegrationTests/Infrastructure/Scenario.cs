using System.Text.Json;
using System.Text.Json.Serialization;
using MyHealth.Application.Exercises;
using MyHealth.Application.Common;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Workouts;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;

namespace MyHealth.Api.IntegrationTests.Infrastructure;

/// <summary>Business-level steps over HTTP, so tests read as the journeys in §3 of the requirements.</summary>
public sealed class Scenario(ApiFactory factory)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ApiFactory Factory => factory;

    public async Task<TestUser> ClientAsync(string name = "client")
    {
        var user = factory.NewUser(name);
        await EnsureSuccess(await user.Http.PostAsJsonAsync("/api/v1/me/profile", new { fullName = $"{name} Silva", timeZoneId = "America/Sao_Paulo" }, Ct));
        return user;
    }

    public async Task<(TestUser User, Guid ProfileId)> ProfessionalAsync(string name = "trainer", Discipline discipline = Discipline.PersonalTrainer, bool verified = true)
    {
        var user = await ClientAsync(name);
        var response = await user.Http.PostAsJsonAsync("/api/v1/professional-profiles", new
        {
            discipline = discipline.ToString(),
            displayName = $"{name} Pro",
            registration = new { council = "CREF", number = Guid.NewGuid().ToString("N")[..8], region = "SP" },
        }, Ct);
        var profileId = (await Read<IdResponse>(response)).Id;

        if (verified)
        {
            var admin = factory.NewUser("admin", administrator: true);
            await EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/professional-profiles/{profileId}/verify", new { notes = "ok" }, Ct));
        }

        return (user, profileId);
    }

    public async Task<Guid> LinkAsync(TestUser professional, TestUser client, Discipline discipline = Discipline.PersonalTrainer)
    {
        var invite = await professional.Http.PostAsJsonAsync("/api/v1/care-relationships/invitations", new { discipline = discipline.ToString(), clientEmail = client.Email }, Ct);
        var relationshipId = (await Read<IdResponse>(invite)).Id;
        await EnsureSuccess(await client.Http.PostAsync($"/api/v1/care-relationships/{relationshipId}/accept", null, Ct));
        return relationshipId;
    }

    public async Task<Guid> AnyLibraryExerciseAsync(TestUser user)
    {
        var page = await Read<PagedResponse<ExerciseResponse>>(await user.Http.GetAsync("/api/v1/exercises?search=supino", Ct));
        return page.Items[0].Id;
    }

    public async Task<Guid> DraftPlanAsync(TestUser trainer, Guid clientId, string name = "Plano A", params string[] sessionNames)
    {
        var create = await trainer.Http.PostAsJsonAsync("/api/v1/training-plans", new
        {
            clientId,
            details = new { name, goal = "Força", level = "Beginner", startDate = "2026-10-01", endDate = (string?)null, suggestedSessionsPerWeek = 3 },
        }, Ct);
        var planId = (await Read<IdResponse>(create)).Id;
        await SetSessionsAsync(trainer, planId, sessionNames.Length == 0 ? ["Treino A"] : sessionNames);
        return planId;
    }

    public async Task SetSessionsAsync(TestUser trainer, Guid planId, IEnumerable<string> sessionNames, IReadOnlyList<PlannedSession>? keep = null)
    {
        var exerciseId = await AnyLibraryExerciseAsync(trainer);
        var kept = (keep ?? []).Select(s => new
        {
            id = (Guid?)s.Id,
            name = s.Name,
            kind = "Strength",
            exercises = s.Exercises.Select(e => new
            {
                id = (Guid?)e.Id, exerciseId = e.ExerciseId, restSeconds = e.RestSeconds,
                sets = e.Sets.Select(t => new { minRepetitions = t.MinRepetitions, maxRepetitions = t.MaxRepetitions, suggestedLoadKg = t.SuggestedLoadKg }).ToArray(),
            }).ToArray(),
        });
        var added = sessionNames.Select(n => new
        {
            id = (Guid?)null,
            name = n,
            kind = "Strength",
            exercises = new[]
            {
                new { id = (Guid?)null, exerciseId, restSeconds = 90, sets = new[] { new { minRepetitions = (int?)8, maxRepetitions = (int?)12, suggestedLoadKg = (decimal?)40m } } },
            },
        });
        await EnsureSuccess(await trainer.Http.PutAsJsonAsync($"/api/v1/training-plans/{planId}/sessions", kept.Concat(added).ToArray(), Ct));
    }

    public async Task<PublishedVersionResponse> PublishAsync(TestUser trainer, Guid planId, string change = "Versão inicial") =>
        await Read<PublishedVersionResponse>(await trainer.Http.PostAsJsonAsync($"/api/v1/training-plans/{planId}/publish", new { changeDescription = change }, Ct));

    public async Task<ClientPlanResponse> MyPlanAsync(TestUser client, Guid planId) =>
        await Read<ClientPlanResponse>(await client.Http.GetAsync($"/api/v1/me/training-plans/{planId}", Ct));

    public async Task<(Guid SessionId, ClientPlanSession Planned)> StartAsync(TestUser client, Guid planId, DateTimeOffset? startedAt = null, int sessionIndex = 0)
    {
        var plan = await MyPlanAsync(client, planId);
        var planned = plan.Sessions[sessionIndex];
        var sessionId = Guid.CreateVersion7();
        await EnsureSuccess(await client.Http.PostAsJsonAsync("/api/v1/workout-sessions", new
        {
            sessionId, planId, plannedSessionId = planned.Session.Id, startedAt = startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-40),
        }, Ct));
        return (sessionId, planned);
    }

    public async Task CompleteAsync(TestUser client, Guid sessionId, DateTimeOffset? completedAt = null, bool discomfort = false) =>
        await EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{sessionId}/complete", new
        {
            completedAt = completedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1), perceivedExertion = 7, reportedDiscomfort = discomfort,
            discomfortDetails = discomfort ? "Dor no joelho" : null,
        }, Ct));

    public static async Task<T> Read<T>(HttpResponseMessage response)
    {
        await EnsureSuccess(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    public static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }
    }

    public sealed record IdResponse(Guid Id);
}
