using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Users;

namespace MyHealth.Domain.UnitTests;

/// <summary>Builders for valid aggregates, so each test states only what it cares about.</summary>
internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    public const string SaoPaulo = "America/Sao_Paulo";
    public static readonly Guid TenantId = Guid.CreateVersion7();

    public static UserProfile Client(string email = "client@example.com") =>
        UserProfile.Create(Guid.CreateVersion7(), "Ana Souza", email, SaoPaulo, Now).Value;

    public static ProfessionalProfile Professional(Discipline discipline = Discipline.PersonalTrainer, bool verified = true)
    {
        var profile = ProfessionalProfile.Create(
            Guid.CreateVersion7(), discipline, "Carlos Lima", null, null, ProfessionalRegistration.Create("CREF", "012345-G", "SP"), Now);
        if (verified)
        {
            profile.Verify(Guid.CreateVersion7(), null, Now);
            profile.ClearDomainEvents();
        }

        return profile;
    }

    public static CareRelationship ActiveRelationship(ProfessionalProfile professional, UserProfile client)
    {
        var relationship = CareRelationship.Invite(TenantId, professional, client.Email, Now).Value;
        relationship.Accept(client, Now);
        relationship.ClearDomainEvents();
        return relationship;
    }

    public static PlanDetails Details(string name = "Hipertrofia A/B") =>
        new(name, "Ganho de massa", TrainingLevel.Intermediate, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31), 3, null);

    public static PlannedSession StrengthSession(string name = "Treino A", Guid? exerciseId = null) =>
        new(
            Guid.CreateVersion7(), name, SessionKind.Strength, 1, null,
            [
                new PlannedExercise(
                    Guid.CreateVersion7(), exerciseId ?? Guid.CreateVersion7(), "Supino reto", 1, 1, null, 90, [],
                    [new SetTarget(1, 8, 12, null, 40m), new SetTarget(2, 8, 12, null, 40m)]),
            ],
            null);

    public static (TrainingPlan Plan, TrainingPlanVersion Version) PublishedPlan(ProfessionalProfile trainer, UserProfile client, params PlannedSession[] sessions)
    {
        var plan = TrainingPlan.Create(TenantId, trainer, client.Id, Details(), Now).Value;
        plan.UpdateDraft(new PlanContent(sessions.Length == 0 ? [StrengthSession()] : sessions), Now);
        var version = plan.Publish(trainer, "Versão inicial", Now).Value;
        plan.ClearDomainEvents();
        return (plan, version);
    }
}
