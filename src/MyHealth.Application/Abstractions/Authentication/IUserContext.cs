namespace MyHealth.Application.Abstractions.Authentication;

/// <summary>The authenticated caller. Endpoints require authentication, so these are always available in handlers.</summary>
public interface IUserContext
{
    Guid UserId { get; }

    string Email { get; }

    bool IsAdministrator { get; }
}
