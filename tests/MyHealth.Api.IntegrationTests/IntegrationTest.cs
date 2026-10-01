using MyHealth.Api.IntegrationTests.Infrastructure;

namespace MyHealth.Api.IntegrationTests;

public abstract class IntegrationTest
{
    // Every test starts by creating users, which skips the test when Docker is unavailable (see ApiFactory.NewUser).
    protected IntegrationTest(ApiFactory factory) => Scenario = new Scenario(factory);

    protected Scenario Scenario { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;
}
