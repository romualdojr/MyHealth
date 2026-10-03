namespace MyHealth.Domain.Professionals;

/// <summary>Licensing council registration, e.g. CREF 012345-G/SP or CRN-3 12345.</summary>
public sealed record ProfessionalRegistration(string Council, string Number, string Region)
{
    public static ProfessionalRegistration Create(string council, string number, string region) =>
        new(council.Trim().ToUpperInvariant(), number.Trim().ToUpperInvariant(), region.Trim().ToUpperInvariant());
}
