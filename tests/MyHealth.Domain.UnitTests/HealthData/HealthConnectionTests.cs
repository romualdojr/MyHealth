using MyHealth.Domain.HealthData;

namespace MyHealth.Domain.UnitTests.HealthData;

public sealed class HealthConnectionTests
{
    [Fact]
    public void Connect_pause_revoke_are_distinct_states_with_history()
    {
        var connection = HealthConnection.Connect(Guid.CreateVersion7(), HealthDataProvider.AppleHealth, TestData.Now);
        connection.AllowsImport.ShouldBeTrue();

        connection.Pause(TestData.Now.AddDays(1));
        connection.AllowsImport.ShouldBeFalse();

        connection.Revoke(TestData.Now.AddDays(2));

        connection.Status.ShouldBe(HealthConnectionStatus.Revoked);
        connection.History.Select(h => h.Status)
            .ShouldBe([HealthConnectionStatus.Connected, HealthConnectionStatus.Paused, HealthConnectionStatus.Revoked]);
    }

    [Fact]
    public void Revoked_connection_cannot_be_paused_but_can_be_reconnected()
    {
        var connection = HealthConnection.Connect(Guid.CreateVersion7(), HealthDataProvider.AppleHealth, TestData.Now);
        connection.Revoke(TestData.Now);

        connection.Pause(TestData.Now).Error.ShouldBe(HealthDataErrors.ConnectionRevoked);

        connection.Reconnect(TestData.Now.AddDays(1));
        connection.AllowsImport.ShouldBeTrue();
    }
}
