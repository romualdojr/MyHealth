using System.Text.Json;
using System.Text.Json.Serialization;
using MyHealth.Application.Exercises;
using MyHealth.Application.Common;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Workouts;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Training.Plans;
using MyHealth.Infrastructure.Identity;

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

    /// <summary>
    /// A professional with a verified profile and their own solo-practice tenant, already selected on their HTTP client.
    /// Pass <paramref name="ownTenant"/> = false for someone who will only join other tenants.
    /// </summary>
    public async Task<(TestUser User, Guid ProfileId)> ProfessionalAsync(
        string name = "trainer", Discipline discipline = Discipline.PersonalTrainer, bool verified = true, bool ownTenant = true, string council = "CREF")
    {
        var user = await ClientAsync(name);
        var response = await user.Http.PostAsJsonAsync("/api/v1/professional-profiles", new
        {
            discipline = discipline.ToString(),
            displayName = $"{name} Pro",
            registration = new { council, number = Guid.NewGuid().ToString("N")[..8], region = "SP" },
        }, Ct);
        var profileId = (await Read<IdResponse>(response)).Id;

        if (verified)
        {
            var admin = factory.NewUser("admin", administrator: true);
            await EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/professional-profiles/{profileId}/verify", new { notes = "ok" }, Ct));
        }

        if (ownTenant)
        {
            UseTenant(user, await CreateTenantAsync(user, $"{name} Pro", TenantKind.SoloPractice, discipline));
        }

        return (user, profileId);
    }

    /// <summary>Creates a tenant with <paramref name="owner"/> as its administrator. Does not select it.</summary>
    public async Task<Guid> CreateTenantAsync(TestUser owner, string name, TenantKind kind, params Discipline[] disciplines)
    {
        var response = await owner.Http.PostAsJsonAsync(
            "/api/v1/tenants", new { name, kind = kind.ToString(), disciplines = disciplines.Select(d => d.ToString()).ToArray() }, Ct);
        return (await Read<IdResponse>(response)).Id;
    }

    /// <summary>Selects the tenant the user acts for, as the app would after the user picks an organization (MT-02).</summary>
    public static void UseTenant(TestUser user, Guid? tenantId)
    {
        user.Http.DefaultRequestHeaders.Remove(TenantHeader.Name);
        if (tenantId is { } id)
        {
            user.Http.DefaultRequestHeaders.Add(TenantHeader.Name, id.ToString());
        }
    }

    /// <summary>The tenant administrator (with their tenant selected) invites <paramref name="member"/>, who accepts.</summary>
    public async Task<Guid> AddMemberAsync(TestUser administrator, TestUser member, params Discipline[] disciplines)
    {
        var invite = await administrator.Http.PostAsJsonAsync("/api/v1/tenant/memberships/invitations", new
        {
            email = member.Email, disciplines = disciplines.Select(d => d.ToString()).ToArray(), isAdministrator = false,
        }, Ct);
        var membershipId = (await Read<IdResponse>(invite)).Id;
        await EnsureSuccess(await member.Http.PostAsync($"/api/v1/tenant-invitations/{membershipId}/accept", null, Ct));
        return membershipId;
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

    /// <summary>The client shares one data category with the professional of this relationship.</summary>
    public static async Task ShareAsync(TestUser client, Guid relationshipId, DataCategory category) =>
        await EnsureSuccess(await client.Http.PutAsync($"/api/v1/care-relationships/{relationshipId}/sharing/{category}", null, Ct));

    /// <summary>A draft meal plan with breakfast and lunch, created by a dietitian (tenant already selected).</summary>
    public async Task<Guid> DraftMealPlanAsync(TestUser dietitian, Guid clientId, string name = "Plano alimentar")
    {
        var create = await dietitian.Http.PostAsJsonAsync("/api/v1/meal-plans", new
        {
            clientId, details = new { name, goal = "Reeducação alimentar", startDate = "2026-10-01" },
        }, Ct);
        var planId = (await Read<IdResponse>(create)).Id;
        await SetMealsAsync(dietitian, planId, "Café da manhã", "Almoço");
        return planId;
    }

    public async Task SetMealsAsync(TestUser dietitian, Guid planId, params string[] mealNames) =>
        await EnsureSuccess(await dietitian.Http.PutAsJsonAsync($"/api/v1/meal-plans/{planId}/meals", mealNames.Select((meal, i) => new
        {
            name = meal,
            time = $"{7 + (i * 5):00}:00:00",
            items = new object[]
            {
                new { food = "Pão integral", quantity = 50m, unit = "g", substitutions = new[] { "Tapioca" } },
                new { food = "Ovo mexido", householdMeasure = "2 unidades" },
            },
        }).ToArray(), Ct));

    public async Task<PublishedVersionResponse> PublishMealPlanAsync(TestUser dietitian, Guid planId, string change = "Versão inicial") =>
        await Read<PublishedVersionResponse>(await dietitian.Http.PostAsJsonAsync($"/api/v1/meal-plans/{planId}/publish", new { changeDescription = change }, Ct));

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
