using System.Net.Http.Headers;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Users;

namespace MyHealth.Api.IntegrationTests;

public sealed class AuthenticationTests(ApiFactory factory) : IntegrationTest(factory)
{
    private sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn);

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        Scenario.Factory.EnsureAvailable();
        using var anonymous = Scenario.Factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_login_and_complete_onboarding_with_real_identity_tokens()
    {
        Scenario.Factory.EnsureAvailable();
        using var http = Scenario.Factory.CreateClient();
        var email = $"real-{Guid.NewGuid():N}@test.local";
        const string password = "Sup3r-Secret!";

        (await http.PostAsJsonAsync("/api/v1/auth/register", new { email, password }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = await Scenario.Read<TokenResponse>(await http.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Ct));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        (await http.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await http.PostAsJsonAsync("/api/v1/me/profile", new { fullName = "Real User", timeZoneId = "America/Sao_Paulo" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var me = await Scenario.Read<MeResponse>(await http.GetAsync("/api/v1/me", Ct));
        me.Email.ShouldBe(email);
        me.IsAdministrator.ShouldBeFalse();
    }

    [Fact]
    public async Task Non_administrators_cannot_use_the_admin_console()
    {
        var user = await Scenario.ClientAsync();

        (await user.Http.GetAsync("/api/v1/admin/audit-entries", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Invalid_input_returns_validation_problem_details()
    {
        var user = Scenario.Factory.NewUser();

        var response = await user.Http.PostAsJsonAsync("/api/v1/me/profile", new { fullName = "", timeZoneId = "Mars/Olympus" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("TimeZoneId");
    }
}
