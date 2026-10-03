namespace MyHealth.Domain.Professionals;

/// <summary>The professional role under which an action is taken.</summary>
public enum Discipline
{
    PersonalTrainer = 1,
    Dietitian = 2,
    TherapyPractitioner = 3,
}

public enum VerificationStatus
{
    Pending = 1,
    Verified = 2,
    Rejected = 3,
    Suspended = 4,
}
