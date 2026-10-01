using MyHealth.Application.Workouts;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Application.UnitTests.Workouts;

public sealed class StartWorkoutHandlerTests
{
    private readonly IUserProfileRepository _users = Substitute.For<IUserProfileRepository>();
    private readonly ITrainingPlanRepository _plans = Substitute.For<ITrainingPlanRepository>();
    private readonly ITrainingPlanVersionRepository _versions = Substitute.For<ITrainingPlanVersionRepository>();
    private readonly IWorkoutSessionRepository _sessions = Substitute.For<IWorkoutSessionRepository>();

    private StartWorkoutHandler HandlerFor(Guid userId) =>
        new(Fixtures.UserContext(userId), _users, _plans, _versions, _sessions, Fixtures.UnitOfWork(), Fixtures.Clock());

    [Fact]
    public async Task Starts_the_current_version_of_the_clients_plan()
    {
        var client = Fixtures.Client();
        var version = Fixtures.PublishedVersion(Fixtures.Trainer(), client.Id, out var plan);
        _users.GetByIdAsync(client.Id, Arg.Any<CancellationToken>()).Returns(client);
        _plans.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        _versions.GetByIdAsync(version.Id, Arg.Any<CancellationToken>()).Returns(version);
        var sessionId = Guid.CreateVersion7();

        var result = await HandlerFor(client.Id).Handle(
            new StartWorkoutCommand(sessionId, plan.Id, version.Content.Sessions[0].Id, Fixtures.Now, null), TestContext.Current.CancellationToken);

        result.Value.ShouldBe(new StartWorkoutResponse(sessionId, version.Id, 1, Created: true));
        _sessions.Received(1).Add(Arg.Is<WorkoutSession>(s => s.Id == sessionId && s.TimeZoneId == client.TimeZoneId));
    }

    [Fact]
    public async Task Retrying_with_the_same_session_id_returns_the_existing_session()
    {
        var client = Fixtures.Client();
        var version = Fixtures.PublishedVersion(Fixtures.Trainer(), client.Id, out var plan);
        var plannedSessionId = version.Content.Sessions[0].Id;
        var existing = WorkoutSession.Start(Guid.CreateVersion7(), client.Id, version, plannedSessionId, Fixtures.Now, "America/Sao_Paulo", Fixtures.Now).Value;
        _sessions.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await HandlerFor(client.Id).Handle(
            new StartWorkoutCommand(existing.Id, plan.Id, plannedSessionId, Fixtures.Now, null), TestContext.Current.CancellationToken);

        result.Value.Created.ShouldBeFalse();
        _sessions.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Session_id_used_by_another_client_is_a_conflict()
    {
        var owner = Fixtures.Client();
        var version = Fixtures.PublishedVersion(Fixtures.Trainer(), owner.Id, out var plan);
        var plannedSessionId = version.Content.Sessions[0].Id;
        var existing = WorkoutSession.Start(Guid.CreateVersion7(), owner.Id, version, plannedSessionId, Fixtures.Now, "America/Sao_Paulo", Fixtures.Now).Value;
        _sessions.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await HandlerFor(Guid.CreateVersion7()).Handle(
            new StartWorkoutCommand(existing.Id, plan.Id, plannedSessionId, Fixtures.Now, null), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(WorkoutErrors.IdConflict);
    }

    [Fact]
    public async Task Another_clients_plan_is_not_found()
    {
        var intruder = Fixtures.Client("intruder@example.com");
        var version = Fixtures.PublishedVersion(Fixtures.Trainer(), Guid.CreateVersion7(), out var plan);
        _users.GetByIdAsync(intruder.Id, Arg.Any<CancellationToken>()).Returns(intruder);
        _plans.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await HandlerFor(intruder.Id).Handle(
            new StartWorkoutCommand(Guid.CreateVersion7(), plan.Id, version.Content.Sessions[0].Id, Fixtures.Now, null), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(PlanErrors.NotFound);
    }
}
