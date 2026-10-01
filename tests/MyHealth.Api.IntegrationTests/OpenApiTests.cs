using System.Text.Json;
using MyHealth.Api.IntegrationTests.Infrastructure;

namespace MyHealth.Api.IntegrationTests;

/// <summary>The API reference is generated from the endpoints; a broken schema would only surface at runtime.</summary>
public sealed class OpenApiTests(ApiFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task The_openapi_document_describes_the_phase_2_and_3_areas()
    {
        Scenario.Factory.EnsureAvailable();
        using var anonymous = Scenario.Factory.CreateClient();

        var response = await anonymous.GetAsync("/openapi/v1.json", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.ShouldContain("/api/v1/me/health-history");
        paths.ShouldContain("/api/v1/physical-assessments");
        paths.ShouldContain("/api/v1/meal-plans/{planId}/publish");
        paths.ShouldContain("/api/v1/me/food-diary/{entryId}");
        paths.ShouldContain("/api/v1/me/data-exports/{exportId}/download");
        paths.ShouldContain("/api/v1/admin/privacy-requests/{requestId}/complete");
        paths.ShouldContain("/api/v1/admin/retention-policies/{recordType}");
        paths.ShouldContain("/api/v1/therapy/sessions/{sessionId}/complete");
        paths.ShouldContain("/api/v1/me/therapy-consents");
        paths.ShouldContain("/api/v1/admin/therapy-modalities/{code}/enable");
    }
}
