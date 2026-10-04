using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Abstractions.Administration;
using MyHealth.Application.Common;
using MyHealth.Application.Exercises;
using MyHealth.Domain.Notifications;
using MyHealth.Infrastructure.Notifications;
using MyHealth.Infrastructure.Persistence;

namespace MyHealth.Api.IntegrationTests;

/// <summary>MFA enforcement (RNF-02), notification delivery (outbox) and the rest of the admin console (RF-50).</summary>
public sealed class SecurityAndOperationsTests(ApiFactory factory) : IntegrationTest(factory)
{
    [Theory]
    [InlineData("127.0.0.1", "203.0.113.7", "ip:203.0.113.7")] // The web dashboard (loopback here) relays the browser's address.
    [InlineData("198.51.100.1", "203.0.113.7", "ip:198.51.100.1")] // Anyone else cannot pick the address they are counted under.
    [InlineData("198.51.100.1", null, "ip:198.51.100.1")]
    public async Task Sign_in_attempts_count_against_the_browser_address_relayed_only_by_a_trusted_proxy(string remote, string? forwarded, string expected)
    {
        Scenario.Factory.EnsureAvailable();
        var options = Scenario.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Path = "/api/v1/auth/login";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remote);
        if (forwarded is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwarded;
        }

        await new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(_ => Task.CompletedTask, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, options)
            .Invoke(context);

        MyHealth.ApiService.Infrastructure.AuthenticationRateLimit.PartitionKey(context).ShouldBe(expected);
    }

    [Fact]
    public void Token_refresh_is_not_limited_and_signed_in_calls_count_per_user()
    {
        var refresh = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        refresh.Request.Path = "/api/v1/auth/refresh";
        MyHealth.ApiService.Infrastructure.AuthenticationRateLimit.PartitionKey(refresh).ShouldBeNull();

        var userId = Guid.NewGuid();
        var signedIn = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, userId.ToString())], "test")),
        };
        signedIn.Request.Path = "/api/v1/auth/manage/2fa";
        MyHealth.ApiService.Infrastructure.AuthenticationRateLimit.PartitionKey(signedIn).ShouldBe($"user:{userId}");
    }

    [Fact]
    public async Task Professionals_and_administrators_need_two_factor_authentication_while_clients_do_not()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();

        var withoutMfa = Scenario.Factory.NewUser("no-mfa", twoFactor: false);
        withoutMfa.Http.DefaultRequestHeaders.Authorization = new("Test", TestAuthHandler.Encode(trainer.Id, trainer.Email, administrator: false, twoFactor: false));
        Scenario.UseTenant(withoutMfa, trainer.TenantId);

        var response = await withoutMfa.Http.GetAsync("/api/v1/trainer/dashboard", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Security.TwoFactorRequired");

        // The same person as a client (no tenant selected) is not blocked.
        Scenario.UseTenant(withoutMfa, null);
        (await withoutMfa.Http.GetAsync("/api/v1/me/home", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var adminWithoutMfa = Scenario.Factory.NewUser("admin", administrator: true, twoFactor: false);
        (await adminWithoutMfa.Http.GetAsync("/api/v1/admin/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var admin = Scenario.Factory.NewUser("admin", administrator: true);
        (await admin.Http.GetAsync("/api/v1/admin/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Notifications_are_pushed_to_registered_devices_or_e_mailed_after_commit()
    {
        var withPhone = await Scenario.ClientAsync("phone");
        var withoutPhone = await Scenario.ClientAsync("email");
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, withPhone);
        await Scenario.LinkAsync(trainer, withoutPhone);
        var installation = Guid.CreateVersion7();
        await Scenario.EnsureSuccess(await withPhone.Http.PutAsJsonAsync($"/api/v1/me/push-devices/{installation}", new { platform = "Ios", token = "apns-token" }, Ct));

        foreach (var client in new[] { withPhone, withoutPhone })
        {
            var plan = await Scenario.DraftPlanAsync(trainer, client.Id);
            await Scenario.PublishAsync(trainer, plan);
        }

        await DispatchAllAsync();

        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var published = await db.Notifications
            .Where(n => (n.RecipientUserId == withPhone.Id || n.RecipientUserId == withoutPhone.Id) && n.Type == NotificationType.TrainingPlanPublished)
            .ToListAsync(Ct);
        published.Count.ShouldBe(2);
        published.ShouldAllBe(n => n.DeliveryStatus == NotificationDeliveryStatus.Delivered && n.DeliveryAttempts == 1 && n.NextDeliveryAttemptAt == null);

        // Knowing an installation id is not enough to take the device over; the phone's own token is.
        (await withoutPhone.Http.PutAsJsonAsync($"/api/v1/me/push-devices/{installation}", new { platform = "Ios", token = "guessed" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Signing out on the phone removes the device; someone else cannot remove it.
        await Scenario.EnsureSuccess(await withoutPhone.Http.DeleteAsync($"/api/v1/me/push-devices/{installation}", Ct));
        (await db.PushDevices.AnyAsync(d => d.InstallationId == installation, Ct)).ShouldBeTrue();
        await Scenario.EnsureSuccess(await withPhone.Http.DeleteAsync($"/api/v1/me/push-devices/{installation}", Ct));
        (await db.PushDevices.AnyAsync(d => d.InstallationId == installation, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Operators_manage_the_curated_exercise_catalog_and_the_operator_role()
    {
        var admin = Scenario.Factory.NewUser("operator", administrator: true);
        var (trainer, _) = await Scenario.ProfessionalAsync();

        var created = await admin.Http.PostAsJsonAsync("/api/v1/admin/exercises", new
        {
            name = "Afundo búlgaro", description = "Pé de trás apoiado no banco.", muscleGroups = new[] { "quadriceps" }, equipment = new[] { "banco" },
        }, Ct);
        var id = (await Scenario.Read<Scenario.IdResponse>(created)).Id;
        (await Scenario.Read<PagedResponse<ExerciseResponse>>(await trainer.Http.GetAsync("/api/v1/exercises?search=búlgaro", Ct))).Items.ShouldContain(e => e.Id == id);

        // Editing a curated entry bumps its version; plans keep the version they were written against.
        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync($"/api/v1/admin/exercises/{id}", new
        {
            name = "Afundo búlgaro", description = "Pé de trás apoiado no banco; desça até 90°.", muscleGroups = new[] { "quadriceps", "gluteos" },
            equipment = new[] { "banco", "halteres" }, precautions = "Joelho alinhado com o pé.",
        }, Ct));
        var edited = await Scenario.Read<ExerciseResponse>(await trainer.Http.GetAsync($"/api/v1/exercises/{id}", Ct));
        (edited.Version, edited.Precautions).ShouldBe((2, "Joelho alinhado com o pé."));
        edited.Equipment.ShouldBe(["banco", "halteres"]);
        (await trainer.Http.PutAsJsonAsync($"/api/v1/admin/exercises/{id}", new { name = "x", description = "x", muscleGroups = Array.Empty<string>(), equipment = Array.Empty<string>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await Scenario.EnsureSuccess(await admin.Http.PostAsync($"/api/v1/admin/exercises/{id}/archive", null, Ct));
        (await Scenario.Read<PagedResponse<ExerciseResponse>>(await trainer.Http.GetAsync("/api/v1/exercises?search=búlgaro", Ct))).Items.ShouldNotContain(e => e.Id == id);
        await Scenario.EnsureSuccess(await admin.Http.PostAsync($"/api/v1/admin/exercises/{id}/restore", null, Ct));

        // A professional's own exercise is not part of the curated catalog.
        var custom = (await Scenario.Read<Scenario.IdResponse>(await trainer.Http.PostAsJsonAsync("/api/v1/exercises", new
        {
            name = "Meu exercício", description = "x", muscleGroups = Array.Empty<string>(), equipment = Array.Empty<string>(),
        }, Ct))).Id;
        (await admin.Http.PostAsync($"/api/v1/admin/exercises/{custom}/archive", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.Http.PutAsJsonAsync($"/api/v1/admin/exercises/{custom}", new { name = "x", description = "x", muscleGroups = Array.Empty<string>(), equipment = Array.Empty<string>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Operator role: unknown accounts are reported, and nobody can remove their own role.
        (await admin.Http.PutAsync($"/api/v1/admin/administrators/{Guid.CreateVersion7()}", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.Http.DeleteAsync($"/api/v1/admin/administrators/{admin.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Scenario.Read<List<AdministratorItem>>(await admin.Http.GetAsync("/api/v1/admin/administrators", Ct))).ShouldNotBeNull();
    }

    private async Task DispatchAllAsync()
    {
        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        while (await dispatcher.DispatchDueAsync(Ct) > 0)
        {
        }
    }
}
