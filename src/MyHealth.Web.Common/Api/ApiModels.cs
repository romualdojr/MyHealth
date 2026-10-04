namespace MyHealth.Web.Common.Api;

// The web dashboard keeps its own copies of the API's response shapes (only the fields it shows), so it can be built
// and deployed on its own. Enums travel as strings, exactly as the API serializes them. A contract test in
// MyHealth.Api.IntegrationTests reads every one of these from the real API, so drift fails the build.

public sealed record TokenResponse(string AccessToken, long ExpiresIn, string RefreshToken);

public sealed record MeResponse(
    Guid UserId, string FullName, string Email, string TimeZoneId, bool IsAdministrator,
    IReadOnlyList<MyProfessionalProfile> ProfessionalProfiles, IReadOnlyList<MyMembership> Memberships);

public sealed record MyProfessionalProfile(Guid Id, string Discipline, string DisplayName, string VerificationStatus);

public sealed record MyMembership(
    Guid MembershipId, Guid TenantId, string TenantName, string Kind, IReadOnlyList<string> Disciplines, bool IsAdministrator, string? SubscriptionStatus);

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

// Billing: organizations and solo practices pay MyHealth per module.

public sealed record ModulePriceItem(string Module, long? MonthlyAmountCents, DateTimeOffset? EffectiveFrom, IReadOnlyList<PriceChange> History);

public sealed record PriceChange(long MonthlyAmountCents, DateTimeOffset EffectiveFrom, DateTimeOffset SetAt);

public sealed record ChargeResponse(string Provider, string? PixCopyPaste, string? CheckoutUrl, DateTimeOffset ExpiresAt);

public sealed record InvoiceLine(string Module, long AmountCents);

public sealed record InvoiceResponse(
    Guid Id, Guid TenantId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, IReadOnlyList<InvoiceLine> Lines, long TotalCents, string Currency,
    DateTimeOffset IssuedAt, DateTimeOffset DueAt, string Status, bool IsOverdue, DateTimeOffset? PaidAt, string? PaymentReference, ChargeResponse? Charge,
    string? VoidReason);

public sealed record TenantBillingItem(
    Guid TenantId, string Name, string Kind, string Status, IReadOnlyList<string> Modules, long MonthlyAmountCents, long OpenAmountCents,
    long OverdueAmountCents, DateTimeOffset TrialEndsAt, DateTimeOffset NextInvoiceAt, DateTimeOffset? LastPaidAt, DateTimeOffset CreatedAt);

public sealed record SubscriptionChange(string Status, DateTimeOffset At, Guid? By, string Reason);

public sealed record TenantBillingDetail(
    TenantBillingItem Summary, IReadOnlyList<string> AdministratorEmails, IReadOnlyList<SubscriptionChange> History, IReadOnlyList<InvoiceResponse> Invoices);

public sealed record RevenueSummary(
    long MonthlyRecurringRevenueCents, IReadOnlyDictionary<string, int> TenantsByStatus, long OpenAmountCents, long OverdueAmountCents,
    long PaidLast30DaysCents, int PaymentsLast30Days, IReadOnlyList<RecentPayment> RecentPayments);

public sealed record RecentPayment(Guid InvoiceId, Guid TenantId, string TenantName, long AmountCents, DateTimeOffset PaidAt, string? Reference);

public sealed record BillingCycleResult(int Subscriptions, int InvoicesIssued, int PastDue, int Suspended, int Active, IReadOnlyList<string> Skipped);

public sealed record WebhookResult(string Outcome);

// Training: the trainer's plan builder, exercise library and workout follow-up.

public sealed record IdResponse(Guid Id);

public sealed record ExerciseMediaItem(Guid Id, string Kind, string? Angle, string Url, string? PosterUrl, int? DurationSeconds);

public sealed record ExerciseItem(
    Guid Id, string Name, string Description, IReadOnlyList<string> MuscleGroups, IReadOnlyList<string> Equipment, string? Purpose,
    string? Precautions, string? MediaCaption, IReadOnlyList<ExerciseMediaItem> Media, int Version, bool IsCustom);

public sealed record TrainerPlanItem(
    Guid Id, Guid ClientId, string Name, string Status, int CurrentVersionNumber, bool HasUnpublishedChanges, DateTimeOffset UpdatedAt, bool NeedsReview);

public sealed record PlanDetails(
    string Name, string Goal, string Level, DateOnly StartDate, DateOnly? EndDate, int SuggestedSessionsPerWeek, string? Notes);

public sealed record SetTarget(int Order, int? MinRepetitions, int? MaxRepetitions, int? DurationSeconds, decimal? SuggestedLoadKg);

public sealed record PlannedExercise(
    Guid Id, Guid ExerciseId, string ExerciseName, int ExerciseVersion, int Order, string? Instructions, int RestSeconds,
    IReadOnlyList<Guid> AlternativeExerciseIds, IReadOnlyList<SetTarget> Sets);

public sealed record AerobicTarget(string Activity, int? DurationMinutes, decimal? DistanceKm, string? Intensity, string? WarmUp, string? CoolDown);

public sealed record PlannedSession(
    Guid Id, string Name, string Kind, int Order, string? Notes, IReadOnlyList<PlannedExercise> Exercises, AerobicTarget? Aerobic);

public sealed record PlanContent(IReadOnlyList<PlannedSession> Sessions);

public sealed record PlanVersionItem(Guid VersionId, int VersionNumber, string ChangeDescription, DateTimeOffset PublishedAt);

public sealed record TrainerPlan(
    Guid Id, Guid ClientId, PlanDetails Details, string Status, PlanContent Draft, bool HasUnpublishedChanges, int CurrentVersionNumber,
    Guid? CopiedFromPlanId, IReadOnlyList<PlanVersionItem> Versions, DateTimeOffset? ReviewRequestedAt, string? ReviewReason);

public sealed record PublishedVersion(Guid VersionId, int VersionNumber, DateTimeOffset PublishedAt);

public sealed record WorkoutSessionItem(
    Guid Id, Guid TrainingPlanId, int PlanVersionNumber, string PlannedSessionName, string Kind, string Status, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, DateOnly? LocalDate, int? PerceivedExertion, bool ReportedDiscomfort, bool HasReply);

public sealed record SetLogItem(
    Guid Id, Guid ExerciseId, Guid? PlannedExerciseId, int SetNumber, int? Repetitions, decimal? LoadKg, int? DurationSeconds,
    decimal? DistanceMeters, int? Difficulty, string? Notes);

public sealed record WorkoutFeedbackItem(int PerceivedExertion, bool ReportedDiscomfort, string? DiscomfortDetails, string? Comments);

public sealed record FeedbackReplyItem(string Message, DateTimeOffset RepliedAt);

public sealed record BodyCheckItem(Guid Id, string Subject, string Answer, string? Details, bool ReportedToTrainer, DateTimeOffset At);

public sealed record CoachSuggestionItem(
    Guid Id, string Kind, Guid? PlannedExerciseId, string? ExerciseName, string Title, string? Change, string Reason, string Status, DateTimeOffset? ResolvedAt);

public sealed record CoachAnalysisItem(
    Guid Id, Guid PlanId, int PlanVersionNumber, string Model, string? Focus, string Summary, IReadOnlyList<string> Notices,
    IReadOnlyList<CoachSuggestionItem> Suggestions, DateTimeOffset CreatedAt);

public sealed record CoachOverview(bool SharingGranted, int DailyLimit, int UsedToday, IReadOnlyList<CoachAnalysisItem> Analyses);

public sealed record StudentSummaryItem(
    Guid ClientId, string Name, Guid RelationshipId, bool AiConsent, Guid? PlanId, string? PlanName, int PlanVersion, int CompletedLast28Days,
    int ExpectedLast28Days, DateTimeOffset? LastWorkoutAt, double? AverageExertion14Days, bool RecentDiscomfort, int PendingFeedback);

public sealed record TrainerAssistantOverviewItem(string Model, IReadOnlyList<StudentSummaryItem> Students);

public sealed record TrainerChatSuggestionItem(Guid AnalysisId, Guid PlanId, Guid ClientId, string ClientName, string PlanName, CoachSuggestionItem Suggestion);

public sealed record TrainerAssistantReply(string Reply, IReadOnlyList<TrainerChatSuggestionItem> Suggestions, int StudentsIncluded, int StudentsWithoutConsent);

/// <summary>One turn sent back to the assistant as history (role "user" or "assistant").</summary>
public sealed record ChatTurn(string Role, string Text);

public sealed record WhatsAppLink(string PhoneNumber, string ChatUrl);

public sealed record MotionCaptureTerms(int Version, string Text);

public sealed record MotionCaptureOutputItem(string Kind, string? Angle, string Format, string Url, long SizeBytes);

public sealed record MotionCaptureJobItem(
    Guid Id, Guid ExerciseId, string Status, int ProgressPercent, string? Error, int DurationSeconds, string SourceFileName,
    IReadOnlyList<MotionCaptureOutputItem> Outputs, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? AcceptedAt, bool SourceDeleted);

public sealed record WorkoutSessionDetail(
    Guid Id, Guid TrainingPlanId, int PlanVersionNumber, string PlannedSessionName, string Kind, string Status, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, IReadOnlyList<SetLogItem> Sets, WorkoutFeedbackItem? Feedback, FeedbackReplyItem? Reply,
    IReadOnlyList<BodyCheckItem> BodyChecks);

// Nutrition: the dietitian's clients, assessments, meal plans and the clients' food diary.

public sealed record DietitianClientSummary(
    Guid ClientId, string ClientName, Guid RelationshipId, Guid? ActivePlanId, string? ActivePlanName, int DraftPlans, int EntriesLast7Days,
    int DaysWithEntriesLast7Days, int AsPlannedLast7Days, int UncommentedEntries, DateTimeOffset? LastEntryAt, DateOnly? LastAssessedOn,
    DateOnly? NextFollowUpOn);

public sealed record DietitianDashboard(IReadOnlyList<DietitianClientSummary> Clients, int TotalUncommentedEntries, int TotalFollowUpsDue, int ClientsWithoutPlan);

public sealed record MeasurementItem(string Metric, string Method, string Unit, decimal Value);

public sealed record NutritionAssessmentContent(
    DateOnly AssessedOn, string? Goals, string? ReportedRestrictions, string? ReportedAllergies, string? Preferences, string? MealTimes,
    IReadOnlyList<MeasurementItem> Measurements, string? FollowUp, DateOnly? NextFollowUpOn, string? Notes);

public sealed record NutritionAssessmentCorrection(int ReplacedVersion, NutritionAssessmentContent PreviousContent, string Reason, DateTimeOffset CorrectedAt);

public sealed record NutritionAssessmentItem(
    Guid Id, Guid ClientId, string DietitianName, NutritionAssessmentContent Content, int Version, IReadOnlyList<NutritionAssessmentCorrection> Corrections,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record MealPlanItem(Guid Id, Guid ClientId, string Name, string Status, int CurrentVersionNumber, bool HasUnpublishedChanges, DateTimeOffset UpdatedAt);

public sealed record MealPlanDetails(string Name, string? Goal, DateOnly StartDate, DateOnly? EndDate, string? Notes);

public sealed record MealItem(Guid Id, string Food, decimal? Quantity, string? Unit, string? HouseholdMeasure, string? Notes, IReadOnlyList<string> Substitutions);

public sealed record PlannedMeal(Guid Id, string Name, TimeOnly? Time, int Order, string? Notes, IReadOnlyList<MealItem> Items);

public sealed record MealPlanContent(IReadOnlyList<PlannedMeal> Meals);

public sealed record MealPlanVersionItem(Guid VersionId, int VersionNumber, string ChangeDescription, string AuthorName, DateTimeOffset PublishedAt);

public sealed record DietitianMealPlan(
    Guid Id, Guid ClientId, MealPlanDetails Details, string Status, MealPlanContent Draft, bool HasUnpublishedChanges, int CurrentVersionNumber,
    IReadOnlyList<MealPlanVersionItem> Versions);

public sealed record MealPlanVersionDetail(
    Guid PlanId, Guid VersionId, int VersionNumber, MealPlanDetails Details, MealPlanContent Content, string ChangeDescription, string AuthorName,
    DateTimeOffset PublishedAt);

public sealed record FoodDiaryCommentItem(Guid Id, string AuthorName, string Message, DateTimeOffset CreatedAt);

public sealed record FoodDiaryEntryItem(
    Guid Id, Guid? MealPlanId, int? MealPlanVersionNumber, Guid? PlannedMealId, string? MealName, DateTimeOffset EatenAt, DateOnly LocalDate,
    string? Description, int? WaterMl, string? Adherence, string? ClientNotes, IReadOnlyList<FoodDiaryCommentItem> Comments);

public sealed record DiaryAdherenceSummary(
    DateOnly From, DateOnly To, int Entries, int DaysWithEntries, int AsPlanned, int Partly, int Different, int Unplanned, int NotStated, int WaterMl);

public sealed record DietitianFoodDiary(IReadOnlyList<FoodDiaryEntryItem> Entries, DiaryAdherenceSummary Summary);

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
        "Trial" => "Período de teste",
        "PastDue" => "Em atraso",
        "Cancelled" => "Cancelado",
        "Open" => "Em aberto",
        "Paid" => "Paga",
        "Void" => "Anulada",
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
        "Verified" or "Enabled" or "Completed" or "Active" or "Paid" => Ui.BadgeSuccess,
        "Trial" => Ui.BadgeInfo,
        "Pending" or "Submitted" or "InProgress" or "PastDue" or "Open" => Ui.BadgeWarning,
        "Rejected" or "Suspended" or "Revoked" or "Disabled" or "Declined" or "Cancelled" => Ui.BadgeDanger,
        _ => Ui.BadgeNeutral,
    };
}

/// <summary>Training labels for display.</summary>
public static class TrainingLabels
{
    public static readonly IReadOnlyList<string> Levels = ["Beginner", "Intermediate", "Advanced"];

    public static string Level(string value) => value switch
    {
        "Beginner" => "Iniciante",
        "Intermediate" => "Intermediário",
        "Advanced" => "Avançado",
        _ => value,
    };

    public static string PlanStatus(string value) => value switch
    {
        "Draft" => "Rascunho",
        "Published" => "Publicado",
        "Ended" => "Encerrado",
        _ => value,
    };

    public static string PlanBadge(string value) => value switch
    {
        "Published" => Ui.BadgeSuccess,
        "Draft" => Ui.BadgeWarning,
        _ => Ui.BadgeNeutral,
    };

    public static string SessionKind(string value) => value switch
    {
        "Strength" => "Musculação",
        "Aerobic" => "Aeróbico",
        _ => value,
    };

    public static string WorkoutStatus(string value) => value switch
    {
        "InProgress" => "Em andamento",
        "Paused" => "Pausado",
        "Completed" => "Concluído",
        "Abandoned" => "Abandonado",
        _ => value,
    };

    public static string BodyCheckAnswer(string value) => value switch
    {
        "Better" => "melhor",
        "Same" => "igual",
        "Worse" => "pior",
        "New" => "novo incômodo",
        _ => value,
    };

    /// <summary>"3 × 8–12 · 20 kg" style summary of an exercise's sets.</summary>
    public static string Sets(IReadOnlyList<SetTarget> sets)
    {
        if (sets.Count == 0)
        {
            return "sem séries";
        }

        var first = sets[0];
        var target = first.MinRepetitions is { } min
            ? (first.MaxRepetitions is { } max && max != min ? $"{min}–{max}" : $"{min}")
            : $"{first.DurationSeconds}s";
        var load = first.SuggestedLoadKg is { } kg ? $" · {kg:0.#} kg" : string.Empty;
        var uniform = sets.All(s => s.MinRepetitions == first.MinRepetitions && s.MaxRepetitions == first.MaxRepetitions
                                    && s.DurationSeconds == first.DurationSeconds && s.SuggestedLoadKg == first.SuggestedLoadKg);
        return uniform ? $"{sets.Count} × {target}{load}" : $"{sets.Count} séries variadas";
    }
}

/// <summary>Billing labels and amounts (cents, BRL) for display.</summary>
public static class BillingLabels
{
    public static readonly IReadOnlyList<string> Modules = ["Training", "Nutrition", "Therapies"];

    public static readonly IReadOnlyList<string> Statuses = ["Trial", "Active", "PastDue", "Suspended", "Cancelled"];

    private static readonly System.Globalization.CultureInfo Brazil = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

    public static string Module(string value) => value switch
    {
        "Training" => "Treino",
        "Nutrition" => "Nutrição",
        "Therapies" => "Terapias",
        _ => value,
    };

    public static string Money(long cents) => (cents / 100m).ToString("C", Brazil);

    public static string Kind(string value) => value switch
    {
        "Organization" => "Organização",
        "SoloPractice" => "Consultório individual",
        _ => value,
    };

    public static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("dd/MM/yyyy", Brazil);
}

/// <summary>Nutrition labels for display.</summary>
public static class NutritionLabels
{
    private static readonly System.Globalization.CultureInfo Brazil = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Units the API accepts for a food portion (free text up to 20 characters; these are the usual ones).</summary>
    public static readonly IReadOnlyList<string> PortionUnits = ["g", "ml", "unidade", "colher de sopa", "colher de chá", "xícara", "fatia", "porção"];

    /// <summary>Measurements a nutrition assessment usually records: metric, method, unit and the Portuguese label.</summary>
    public static readonly IReadOnlyList<(string Metric, string Method, string Unit, string Label)> CommonMeasurements =
    [
        ("body_mass", "scale", "kg", "Peso (kg)"),
        ("height", "stadiometer", "cm", "Altura (cm)"),
        ("waist_circumference", "tape", "cm", "Cintura (cm)"),
        ("hip_circumference", "tape", "cm", "Quadril (cm)"),
        ("body_fat", "bioimpedance", "%", "Gordura corporal (%, bioimpedância)"),
    ];

    public static string Measurement(MeasurementItem m) =>
        CommonMeasurements.FirstOrDefault(c => c.Metric == m.Metric && c.Method == m.Method && c.Unit == m.Unit) is { Label: not null } known
            ? $"{known.Label.Split(" (")[0]}: {m.Value.ToString("0.##", Brazil)} {m.Unit}"
            : $"{m.Metric.Replace('_', ' ')} ({m.Method.Replace('_', ' ')}): {m.Value.ToString("0.##", Brazil)} {m.Unit}";

    public static string Adherence(string? value) => value switch
    {
        "AsPlanned" => "como planejado",
        "Partly" => "em parte",
        "Different" => "diferente do plano",
        "Unplanned" => "fora do plano",
        _ => "não informado",
    };

    /// <summary>Neutral colours on purpose: the diary never sounds punitive (RF-30).</summary>
    public static string AdherenceBadge(string? value) => value switch
    {
        "AsPlanned" => Ui.BadgeSuccess,
        "Partly" => Ui.BadgeInfo,
        _ => Ui.BadgeNeutral,
    };

    /// <summary>"100 g", "1 xícara", or "100 g (1 xícara)" when both are given.</summary>
    public static string Portion(MealItem item)
    {
        var quantity = item.Quantity is { } q && !string.IsNullOrWhiteSpace(item.Unit) ? $"{q.ToString("0.##", Brazil)} {item.Unit}" : null;
        return (quantity, item.HouseholdMeasure) switch
        {
            ({ } a, { } b) => $"{a} ({b})",
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => string.Empty,
        };
    }
}
