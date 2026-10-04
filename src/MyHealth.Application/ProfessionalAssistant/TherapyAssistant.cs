using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Common;
using MyHealth.Application.TrainerAssistant;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;

namespace MyHealth.Application.ProfessionalAssistant;

public sealed record CarePlanSummary(
    string ModalityCode,
    string Goals,
    int? PlannedSessions,
    string? Frequency,
    DateOnly StartedOn,
    DateOnly? NextReviewOn,
    CarePlanStatus Status,
    CarePlanReviewOutcome? LastReviewOutcome,
    string? LastReviewNotes);

/// <summary>A completed session with its care record and how the client said they felt (1–5, self-reported).</summary>
public sealed record SessionNote(
    DateOnly Date,
    string Service,
    string? ReportedConcern,
    string? ProfessionalAssessment,
    string? ContraindicationsAndPrecautions,
    string? Technique,
    string? PointsOrRegions,
    string? AdverseEvents,
    string? PostSessionGuidance,
    int? ClientWellbeing,
    string? ClientComments);

public sealed record TherapyPatientContext(
    IReadOnlyList<CarePlanSummary> CarePlans, int CompletedSessionsLast90Days, int ScheduledSessions, DateOnly? LastSession, IReadOnlyList<SessionNote> RecentSessions);

/// <summary>The practitioner's patients: care plans and recent session records; proposals are care plans in a modality they are qualified for.</summary>
internal sealed partial class TherapyAssistantWorkspace(IReadDbContext db, TimeProvider clock) : AssistantWorkspace
{
    public const string ProposeCarePlan = ProfessionalAssistantTools.ProposeCarePlan;
    private const int SessionDays = 90;
    private const int RecentSessions = 6;
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public override Discipline Discipline => Discipline.TherapyPractitioner;

    public override string AuditResourceType => "TherapyAssistant";

    public override string SystemPrompt => """
        You are the MyHealth assistant for a practitioner of integrative and complementary therapies (for example acupuncture) in Brazil,
        registered with a professional council. Answer in Brazilian Portuguese, concisely, for a health professional. You see the
        practitioner's patients who allow AI analysis, in the JSON context: each with an alias ("Paciente 1", "Paciente 2"…) and, under
        "therapy", their care plans (modality, goals, planned sessions, frequency, reviews), how many sessions they had in the last 90 days,
        and the records of the latest sessions (reported concern, the practitioner's assessment, precautions, technique, adverse events and
        how the patient said they felt, 1–5). "modalities" lists the modalities the practitioner is qualified for. "focusAlias" is the patient
        the practitioner is looking at, if any. "patientsWithoutConsent" patients exist but are not shared with you.

        What you do:
        - Summaries of one patient or of all: sessions done, how the patient reports feeling, reviews due, adverse events, what needs attention.
        - Help create care plans: when asked, call propose_care_plan with a modality code from "modalities", clear and measurable goals
          based on the records, the number of sessions, the frequency and a review date. The practitioner reviews the proposal and starts the
          plan; nothing is created before that. If the patient already has an active plan in that modality, suggest reviewing it instead.
        - Answer questions about the care plans and the records.

        Rules you must never break:
        - Always refer to patients by their alias exactly as written ("Paciente 2"); never invent or ask for names.
        - No diagnosis, no promise of cure, no medication advice, and never suggest replacing medical treatment.
        - After adverse events or warning signs in the records, do not propose a new or more intensive plan: say the practitioner should
          review the case and, if needed, refer the patient for medical care.
        - Use only data and modality codes in the context. If the data is not there, say so.
        - At most 3 propose_care_plan calls per answer. Do not say a plan was started: the practitioner starts it.
        """;

    public override IReadOnlyList<AssistantToolDefinition> Tools { get; } =
    [
        Define(ProposeCarePlan, "Propose a care plan for one patient. The practitioner reviews it and starts it.", """
            {
              "type": "object",
              "properties": {
                "patient": { "type": "string", "description": "The patient's alias, e.g. \"Paciente 1\"." },
                "modality_code": { "type": "string", "description": "A code from \"modalities\"." },
                "goals": { "type": "string" },
                "planned_sessions": { "type": "integer", "minimum": 1, "maximum": 200 },
                "frequency": { "type": "string", "description": "e.g. \"1 vez por semana\"" },
                "next_review_on": { "type": "string", "description": "yyyy-MM-dd" },
                "reason": { "type": "string", "description": "Why this plan, citing the records." }
              },
              "required": ["patient", "modality_code", "goals", "reason"]
            }
            """),
    ];

    public override async Task<WorkspaceData> LoadAsync(ActingProfessional professional, CancellationToken cancellationToken)
    {
        var tenantId = professional.TenantId;
        var profileId = professional.Profile.Id;
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // The modalities the practitioner may start a care plan in: enabled, with a verified qualification.
        var modalities = await db.TherapyModalities.ToDictionaryAsync(m => m.Id, cancellationToken);
        var qualified = await db.PractitionerQualifications
            .Where(q => q.ProfessionalProfileId == profileId && q.Status == QualificationStatus.Verified)
            .Select(q => q.ModalityId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var mine = qualified
            .Select(id => modalities.GetValueOrDefault(id))
            .Where(m => m is { Status: ModalityStatus.Enabled })
            .Select(m => new AssistantModality(m!.Code, m.Name))
            .OrderBy(m => m.Name, StringComparer.CurrentCulture)
            .ToList();

        var relationships = await db.CareRelationships
            .Where(r => r.TenantId == tenantId && r.ProfessionalProfileId == profileId && r.Status == RelationshipStatus.Active && r.ClientId != null)
            .ToListAsync(cancellationToken);
        var clientIds = relationships.Select(r => r.ClientId!.Value).ToList();
        if (clientIds.Count == 0)
        {
            return new WorkspaceData([], mine);
        }

        var names = await db.UserProfiles.Where(u => clientIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        var plans = (await db.TherapyCarePlans
                .Where(p => p.TenantId == tenantId && p.PractitionerProfileId == profileId && clientIds.Contains(p.ClientId))
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.ClientId);
        var since = now.AddDays(-SessionDays);
        var sessions = (await db.TherapySessions
                .Where(s => s.TenantId == tenantId && s.PractitionerProfileId == profileId && clientIds.Contains(s.ClientId) && s.StartsAt >= since)
                .ToListAsync(cancellationToken))
            .ToLookup(s => s.ClientId);

        string Code(Guid modalityId) => modalities.GetValueOrDefault(modalityId)?.Code ?? "desconhecida";
        string Name(Guid modalityId) => modalities.GetValueOrDefault(modalityId)?.Name ?? "Modalidade";

        var patients = relationships
            .Select(r =>
            {
                var clientId = r.ClientId!.Value;
                var carePlans = plans[clientId].OrderByDescending(p => p.Status == CarePlanStatus.Active).ThenByDescending(p => p.UpdatedAt).ToList();
                var active = carePlans.FirstOrDefault(p => p.Status == CarePlanStatus.Active);
                var completed = sessions[clientId].Where(s => s.Status == TherapySessionStatus.Completed).OrderByDescending(s => s.StartsAt).ToList();
                var scheduled = sessions[clientId].Count(s => s.Status == TherapySessionStatus.Scheduled && s.StartsAt >= now);

                var context = new TherapyPatientContext(
                    [
                        .. carePlans.Select(p => new CarePlanSummary(
                            Code(p.ModalityId), p.Goals, p.PlannedSessions, p.Frequency, DateOnly.FromDateTime(p.CreatedAt.UtcDateTime), p.NextReviewOn, p.Status,
                            p.Reviews.LastOrDefault()?.Outcome, p.Reviews.LastOrDefault()?.Notes)),
                    ],
                    completed.Count,
                    scheduled,
                    completed.Count > 0 ? DateOnly.FromDateTime(completed[0].StartsAt.UtcDateTime) : null,
                    [
                        .. completed.Take(RecentSessions).Select(s => new SessionNote(
                            DateOnly.FromDateTime(s.StartsAt.UtcDateTime), s.ServiceName, s.Record?.ReportedConcern, s.Record?.ProfessionalAssessment,
                            s.Record?.ContraindicationsAndPrecautions, s.Record?.Technique, s.Record?.PointsOrRegions, AdverseEvent(s.Record?.AdverseEvents),
                            s.Record?.PostSessionGuidance, s.ClientReport?.Wellbeing, s.ClientReport?.Comments)),
                    ]);

                var detail = $"{completed.Count} sessão(ões) em {SessionDays} dias";
                if (active?.NextReviewOn is { } review)
                {
                    detail += $" · revisão {review.ToString("dd/MM", Brazil)}";
                }

                string? attention = null;
                if (completed.Any(s => AdverseEvent(s.Record?.AdverseEvents) is not null && s.StartsAt >= now.AddDays(-30)))
                {
                    attention = "evento adverso";
                }
                else if (active?.NextReviewOn < today)
                {
                    attention = "revisão atrasada";
                }
                else if (active is null)
                {
                    attention = "sem plano";
                }

                var summary = new PatientSummary(
                    clientId, names.GetValueOrDefault(clientId, "Paciente"), r.Id, r.HasActiveGrant(DataCategory.AiCoaching), active?.Id,
                    active is null ? null : Name(active.ModalityId), detail, attention);
                return new PatientEntry(summary, Therapy: context);
            })
            .OrderBy(p => p.Summary.Name, StringComparer.CurrentCulture)
            .ToList();
        return new WorkspaceData(patients, mine);
    }

    /// <summary>
    /// The adverse events written in a record, or <c>null</c> when there were none: the field is mandatory in some
    /// modalities, so practitioners often write "Nenhum" or "Sem intercorrências".
    /// </summary>
    internal static string? AdverseEvent(string? text) =>
        string.IsNullOrWhiteSpace(text) || NoAdverseEvent().IsMatch(text.Trim()) ? null : text.Trim();

    [GeneratedRegex(@"^(nenhum|nenhuma|nada|n[aã]o houve|sem intercorr[eê]ncias?|sem eventos?( adversos?)?|n/?a|-+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex NoAdverseEvent();

    public override AssistantProposals Proposals(
        IReadOnlyList<AssistantToolCall> calls, IReadOnlyList<IncludedPatient> included, ProfessionalAssistantContext context, WorkspaceData data,
        StudentAliases aliases)
    {
        var proposals = new List<CarePlanProposal>();
        foreach (var call in calls.Where(c => c.Name == ProposeCarePlan))
        {
            if (proposals.Count == MaxProposals || Read<ProposeCarePlanInput>(call) is not { } input || PatientOf(input.Patient, included, context) is not { } patient)
            {
                continue;
            }

            // Only a modality the practitioner may start a plan in, and goals the care plan accepts.
            var modality = (data.Modalities ?? []).FirstOrDefault(m => string.Equals(m.Code, input.ModalityCode?.Trim(), StringComparison.OrdinalIgnoreCase));
            var goals = Clean(aliases.Reveal(input.Goals ?? string.Empty), 4000);
            if (modality is null || goals is null)
            {
                continue;
            }

            var nextReview = DateOnly.TryParseExact(input.NextReviewOn ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var on)
                             && on >= context.Today
                ? on
                : context.Today.AddDays(30);
            var summary = patient.Entry.Summary;
            proposals.Add(new CarePlanProposal(
                summary.ClientId, summary.Name, modality.Code, modality.Name, goals, input.PlannedSessions is >= 1 and <= 200 ? input.PlannedSessions : null,
                Clean(input.Frequency, 200), nextReview, Clean(aliases.Reveal(input.Reason ?? string.Empty), 2000)));
        }

        return new AssistantProposals([], proposals);
    }

    private sealed record ProposeCarePlanInput(
        string? Patient, string? ModalityCode, string? Goals, int? PlannedSessions, string? Frequency, string? NextReviewOn, string? Reason);
}
