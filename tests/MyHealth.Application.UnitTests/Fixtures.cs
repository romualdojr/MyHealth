using Microsoft.Extensions.Time.Testing;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Users;

namespace MyHealth.Application.UnitTests;

internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Clock() => new(Now);

    public static IUserContext UserContext(Guid userId, string email = "user@example.com")
    {
        var user = Substitute.For<IUserContext>();
        user.UserId.Returns(userId);
        user.Email.Returns(email);
        return user;
    }

    public static IUnitOfWork UnitOfWork() => Substitute.For<IUnitOfWork>();

    public static IAuditLog AuditLog() => Substitute.For<IAuditLog>();

    public static UserProfile Client(string email = "client@example.com") =>
        UserProfile.Create(Guid.CreateVersion7(), "Ana Souza", email, "America/Sao_Paulo", Now).Value;

    public static ProfessionalProfile Trainer(bool verified = true)
    {
        var profile = ProfessionalProfile.Create(
            Guid.CreateVersion7(), Discipline.PersonalTrainer, "Carlos", null, null, ProfessionalRegistration.Create("CREF", "1", "SP"), Now);
        if (verified)
        {
            profile.Verify(Guid.CreateVersion7(), null, Now);
        }

        return profile;
    }

    public static CareRelationship ActiveRelationship(ProfessionalProfile professional, UserProfile client)
    {
        var relationship = CareRelationship.Invite(professional, client.Email, Now).Value;
        relationship.Accept(client, Now);
        return relationship;
    }

    public static TrainingPlanVersion PublishedVersion(ProfessionalProfile trainer, Guid clientId, out TrainingPlan plan)
    {
        plan = TrainingPlan.Create(
            trainer, clientId, new PlanDetails("A", "Força", TrainingLevel.Beginner, new DateOnly(2026, 10, 1), null, 3, null), Now).Value;
        plan.UpdateDraft(new PlanContent(
        [
            new PlannedSession(Guid.CreateVersion7(), "Treino A", SessionKind.Strength, 1, null,
                [new PlannedExercise(Guid.CreateVersion7(), Guid.CreateVersion7(), "Supino", 1, 1, null, 60, [], [new SetTarget(1, 10, 10, null, null)])],
                null),
        ]), Now);
        return plan.Publish(trainer, "v1", Now).Value;
    }
}
