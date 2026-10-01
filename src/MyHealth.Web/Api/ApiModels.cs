namespace MyHealth.Web.Api;

// The web dashboard keeps its own copies of the API's response shapes (only the fields it shows), so it can be built
// and deployed on its own. Enums travel as strings, exactly as the API serializes them. A contract test in
// MyHealth.Api.IntegrationTests reads every one of these from the real API, so drift fails the build.

public sealed record TokenResponse(string AccessToken, long ExpiresIn, string RefreshToken);

public sealed record MeResponse(
    Guid UserId, string FullName, string Email, string TimeZoneId, bool IsAdministrator,
    IReadOnlyList<MyProfessionalProfile> ProfessionalProfiles, IReadOnlyList<MyMembership> Memberships);

public sealed record MyProfessionalProfile(Guid Id, string Discipline, string DisplayName, string VerificationStatus);

public sealed record MyMembership(Guid MembershipId, Guid TenantId, string TenantName, string Kind, IReadOnlyList<string> Disciplines, bool IsAdministrator);

public sealed record TwoFactorStatus(string SharedKey, int RecoveryCodesLeft, IReadOnlyList<string>? RecoveryCodes, bool IsTwoFactorEnabled);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record ProfessionalProfileItem(
    Guid Id, Guid UserId, string Discipline, string DisplayName, string? Bio, string Council, string RegistrationNumber, string Region,
    string VerificationStatus, DateTimeOffset? VerifiedAt, DateTimeOffset CreatedAt);

public sealed record QualificationItem(
    Guid Id, Guid ProfessionalProfileId, string PractitionerName, string Council, string RegistrationNumber, string Region, string ModalityCode,
    string Title, string Issuer, string? CertificateNumber, DateOnly? IssuedOn, string Status, DateTimeOffset SubmittedAt, DateTimeOffset? ReviewedAt,
    string? ReviewNotes);

public sealed record ModalityItem(
    Guid Id, string Code, string Name, string? Description, IReadOnlyList<string> AcceptedCouncils, IReadOnlyList<string> RequiredRecordFields,
    bool RequiresConsent, string Status, string? LegalReviewReference, DateTimeOffset? EnabledAt, int? CurrentConsentVersion);

public sealed record PrivacyRequestItem(
    Guid Id, Guid UserId, string? UserEmail, string Type, string Status, string? Details, DateTimeOffset SubmittedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? ResolvedAt, string? ResolutionNotes, int DeliveryCount, DateTimeOffset? LastDeliveredAt);

public sealed record RetentionPolicyItem(string RecordType, int? RetentionMonths, string? LegalBasis, string? Notes, DateTimeOffset? UpdatedAt);

public sealed record AuditEntryItem(
    Guid Id, DateTimeOffset OccurredAt, Guid ActorUserId, string Action, string ResourceType, Guid ResourceId, Guid? SubjectUserId, Guid? TenantId);

public sealed record TenantSummaryItem(Guid Id, string Name, string Kind, DateTimeOffset CreatedAt, int ActiveMembers);

public sealed record AdministratorItem(Guid UserId, string Email, bool TwoFactorEnabled);

public sealed record MembershipItem(
    Guid Id, Guid? UserId, string Email, string? FullName, IReadOnlyList<string> Disciplines, bool IsAdministrator, string Status, DateTimeOffset InvitedAt,
    DateTimeOffset? JoinedAt, DateTimeOffset? RevokedAt);

public sealed record TrainerClientSummary(
    Guid ClientId, string ClientName, Guid RelationshipId, string? ActivePlanName, int? SuggestedSessionsPerWeek, int CompletedLast7Days,
    double? AdherenceLast7Days, int PendingFeedbackReplies, int UnrepliedDiscomfortReports, DateTimeOffset? LastCompletedAt, int PlansNeedingReview);

public sealed record TrainerDashboard(IReadOnlyList<TrainerClientSummary> Clients, int TotalPendingFeedback, int TotalDiscomfortReports, int TotalPlansNeedingReview);

/// <summary>Disciplines and council-facing labels in Portuguese, for display only.</summary>
public static class Labels
{
    public static string Discipline(string value) => value switch
    {
        "PersonalTrainer" => "Personal trainer",
        "Dietitian" => "Nutricionista",
        "TherapyPractitioner" => "Terapeuta",
        _ => value,
    };

    public static string Status(string value) => value switch
    {
        "Pending" => "Pendente",
        "Verified" => "Verificado",
        "Rejected" => "Rejeitado",
        "Suspended" => "Suspenso",
        "Revoked" => "Revogado",
        "Enabled" => "Habilitada",
        "Disabled" => "Desabilitada",
        "Submitted" => "Recebida",
        "InProgress" => "Em andamento",
        "Completed" => "Concluída",
        "Active" => "Ativo",
        "Declined" => "Recusado",
        _ => value,
    };

    public static string PrivacyType(string value) => value switch
    {
        "DataExport" => "Exportação de dados",
        "Correction" => "Correção",
        "Deletion" => "Exclusão",
        _ => value,
    };

    public static string BadgeClass(string status) => status switch
    {
        "Verified" or "Enabled" or "Completed" or "Active" => "text-bg-success",
        "Pending" or "Submitted" or "InProgress" => "text-bg-warning",
        "Rejected" or "Suspended" or "Revoked" or "Disabled" or "Declined" => "text-bg-danger",
        _ => "text-bg-secondary",
    };
}
