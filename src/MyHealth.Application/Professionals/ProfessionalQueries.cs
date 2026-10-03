using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.Professionals;

public sealed record ProfessionalProfileResponse(
    Guid Id,
    Guid UserId,
    Discipline Discipline,
    string DisplayName,
    string? Bio,
    string? PhotoUrl,
    string Council,
    string RegistrationNumber,
    string Region,
    VerificationStatus VerificationStatus,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset CreatedAt);

/// <summary>Admin review queue (RF-50). Contains professional data only, never client data.</summary>
public sealed record ListProfessionalProfilesQuery(VerificationStatus? Status) : IQuery<IReadOnlyList<ProfessionalProfileResponse>>;

internal sealed class ListProfessionalProfilesHandler(IReadDbContext db)
    : IQueryHandler<ListProfessionalProfilesQuery, IReadOnlyList<ProfessionalProfileResponse>>
{
    public async Task<Result<IReadOnlyList<ProfessionalProfileResponse>>> Handle(
        ListProfessionalProfilesQuery query, CancellationToken cancellationToken)
    {
        var profiles = db.ProfessionalProfiles.AsQueryable();
        if (query.Status is { } status)
        {
            profiles = profiles.Where(p => p.VerificationStatus == status);
        }

        var items = await profiles
            .OrderBy(p => p.CreatedAt)
            .Select(p => new ProfessionalProfileResponse(
                p.Id, p.UserId, p.Discipline, p.DisplayName, p.Bio, p.PhotoUrl, p.Registration.Council, p.Registration.Number,
                p.Registration.Region, p.VerificationStatus, p.VerifiedAt, p.CreatedAt))
            .ToListAsync(cancellationToken);

        return items;
    }
}
