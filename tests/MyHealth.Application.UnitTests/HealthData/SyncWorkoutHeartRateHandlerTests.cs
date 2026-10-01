using MyHealth.Application.HealthData;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.UnitTests.HealthData;

public sealed class SyncWorkoutHeartRateHandlerTests
{
    private readonly IWorkoutSessionRepository _sessions = Substitute.For<IWorkoutSessionRepository>();
    private readonly IWorkoutHeartRateRepository _heartRates = Substitute.For<IWorkoutHeartRateRepository>();
    private readonly IHealthConnectionRepository _connections = Substitute.For<IHealthConnectionRepository>();
    private readonly WorkoutSession _session;
    private readonly Guid _clientId = Guid.CreateVersion7();

    public SyncWorkoutHeartRateHandlerTests()
    {
        var version = Fixtures.PublishedVersion(Fixtures.Trainer(), _clientId, out _);
        _session = WorkoutSession.Start(
            Guid.CreateVersion7(), _clientId, version, version.Content.Sessions[0].Id, Fixtures.Now.AddMinutes(-30), "America/Sao_Paulo", Fixtures.Now).Value;
        _sessions.GetByIdAsync(_session.Id, Arg.Any<CancellationToken>()).Returns(_session);
    }

    private SyncWorkoutHeartRateHandler HandlerFor(Guid userId) =>
        new(Fixtures.UserContext(userId), _sessions, _heartRates, _connections, Fixtures.AuditLog(), Fixtures.UnitOfWork(), Fixtures.Clock());

    private SyncWorkoutHeartRateCommand Report(DateTimeOffset? endedAt, params HeartRatePointInput[] points) =>
        new(_session.Id, _session.StartedAt, endedAt, points);

    private static HeartRatePointInput Point(DateTimeOffset at, double bpm = 120) => new(Guid.CreateVersion7(), at, bpm, "Apple Watch", null);

    [Fact]
    public async Task Upload_is_refused_when_apple_health_is_not_connected()
    {
        var result = await HandlerFor(_clientId).Handle(Report(null, Point(Fixtures.Now)), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(HealthDataErrors.ImportNotAllowed);
    }

    [Fact]
    public async Task Upload_is_refused_while_the_connection_is_paused()
    {
        var connection = HealthConnection.Connect(_clientId, HealthDataProvider.AppleHealth, Fixtures.Now);
        connection.Pause(Fixtures.Now);
        _connections.GetAsync(_clientId, HealthDataProvider.AppleHealth, Arg.Any<CancellationToken>()).Returns(connection);

        var result = await HandlerFor(_clientId).Handle(Report(null, Point(Fixtures.Now)), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(HealthDataErrors.ImportNotAllowed);
    }

    [Fact]
    public async Task Another_user_cannot_upload_to_this_session()
    {
        var result = await HandlerFor(Guid.CreateVersion7()).Handle(Report(null, Point(Fixtures.Now)), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(WorkoutErrors.NotFound);
    }

    [Fact]
    public async Task Reconciliation_is_refused_after_the_30_day_window()
    {
        _connections.GetAsync(_clientId, HealthDataProvider.AppleHealth, Arg.Any<CancellationToken>())
            .Returns(HealthConnection.Connect(_clientId, HealthDataProvider.AppleHealth, Fixtures.Now));
        _session.Complete(new WorkoutFeedback(6, false, null, null), Fixtures.Now, Fixtures.Now);
        var clock = Fixtures.Clock();
        clock.Advance(TimeSpan.FromDays(31));
        var handler = new SyncWorkoutHeartRateHandler(
            Fixtures.UserContext(_clientId), _sessions, _heartRates, _connections, Fixtures.AuditLog(), Fixtures.UnitOfWork(), clock);

        var result = await handler.Handle(Report(Fixtures.Now, Point(Fixtures.Now.AddMinutes(-10))), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(HealthDataErrors.ReconciliationWindowClosed);
    }

    [Fact]
    public async Task Samples_outside_the_server_session_interval_are_rejected()
    {
        _connections.GetAsync(_clientId, HealthDataProvider.AppleHealth, Arg.Any<CancellationToken>())
            .Returns(HealthConnection.Connect(_clientId, HealthDataProvider.AppleHealth, Fixtures.Now));
        _session.Complete(new WorkoutFeedback(6, false, null, null), Fixtures.Now, Fixtures.Now);

        var result = await HandlerFor(_clientId).Handle(
            Report(Fixtures.Now, Point(Fixtures.Now.AddMinutes(-10)), Point(Fixtures.Now.AddMinutes(10))),
            TestContext.Current.CancellationToken);

        result.Value.Accepted.ShouldBe(1);
        result.Value.Rejected.ShouldBe(1);
        result.Value.IsFinalized.ShouldBeTrue();
        _heartRates.Received(1).Add(Arg.Is<WorkoutHeartRate>(h => h.Id == _session.Id));
    }
}
