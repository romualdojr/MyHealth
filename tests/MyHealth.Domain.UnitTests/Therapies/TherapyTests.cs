using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;

namespace MyHealth.Domain.UnitTests.Therapies;

public sealed class TherapyTests
{
    private static readonly TherapyRecord FullRecord = new("Dor", "Avaliação", "Nenhuma", "Sistêmica", null, 30, "Nenhum", "Hidratar-se");

    private static ProfessionalProfile Practitioner(string council = "COFFITO", bool verified = true)
    {
        var profile = ProfessionalProfile.Create(
            Guid.CreateVersion7(), Discipline.TherapyPractitioner, "Paula", null, null, ProfessionalRegistration.Create(council, "1", "SP"), TestData.Now);
        if (verified)
        {
            profile.Verify(Guid.CreateVersion7(), null, TestData.Now);
            profile.ClearDomainEvents();
        }

        return profile;
    }

    private static TherapyModality Acupuncture(bool enabled = true, bool consent = true)
    {
        var modality = TherapyModality.Define("acupuncture", "Acupuntura", null, ["coffito", "CFM"], TherapyModality.AcupunctureDefaultRequiredFields, consent, TestData.Now).Value;
        if (enabled)
        {
            modality.Enable("Parecer 1", Guid.CreateVersion7(), TestData.Now);
        }

        return modality;
    }

    private static PractitionerQualification Qualified(ProfessionalProfile practitioner, TherapyModality modality)
    {
        var qualification = PractitionerQualification.Submit(practitioner, modality, "Especialização", "Instituto", null, null, TestData.Now).Value;
        qualification.Verify(practitioner, modality, Guid.CreateVersion7(), null, TestData.Now);
        return qualification;
    }

    private static (TherapySession Session, ProfessionalProfile Practitioner, TherapyModality Modality, PractitionerQualification Qualification, ConsentTemplate Template)
        Scheduled(bool consent = true)
    {
        var practitioner = Practitioner();
        var modality = Acupuncture(consent: consent);
        var qualification = Qualified(practitioner, modality);
        var service = TherapyService.Offer(TestData.TenantId, practitioner, modality, qualification, "Sessão", null, 50, ServiceDelivery.InPerson, TestData.Now).Value;
        var session = TherapySession.Schedule(Guid.CreateVersion7(), practitioner, modality, qualification, service, TestData.Now.AddMinutes(-5), TestData.Now).Value;
        var template = ConsentTemplate.Publish(modality, 0, "Termo", "Texto", ["Dor local"], Guid.CreateVersion7(), TestData.Now).Value;
        return (session, practitioner, modality, qualification, template);
    }

    [Fact]
    public void A_modality_is_enabled_only_with_councils_and_a_legal_review_and_reconfiguring_disables_it()
    {
        var modality = TherapyModality.Define("acupuncture", "Acupuntura", null, [], [], true, TestData.Now).Value;

        modality.Enable("Parecer 1", Guid.CreateVersion7(), TestData.Now).Error.ShouldBe(TherapyErrors.NoAcceptedCouncils);
        modality.Configure("Acupuntura", null, ["cfm"], [TherapyRecordField.Technique], true, TestData.Now);
        modality.Enable(" ", Guid.CreateVersion7(), TestData.Now).Error.ShouldBe(TherapyErrors.LegalReviewRequired);
        modality.Enable("Parecer 1", Guid.CreateVersion7(), TestData.Now).IsSuccess.ShouldBeTrue();

        modality.Configure("Acupuntura (nome novo)", null, ["CFM"], [TherapyRecordField.Technique], true, TestData.Now);
        modality.IsEnabled.ShouldBeTrue();
        modality.Configure("Acupuntura", null, ["CFM", "COFFITO"], [TherapyRecordField.Technique], true, TestData.Now);
        modality.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Care_needs_a_verified_profile_an_enabled_modality_a_council_in_scope_and_a_verified_qualification()
    {
        var modality = Acupuncture();
        var practitioner = Practitioner();
        var qualification = Qualified(practitioner, modality);

        TherapyRules.EnsureCanProvideCare(practitioner, modality, qualification).IsSuccess.ShouldBeTrue();
        TherapyRules.EnsureCanProvideCare(Practitioner(verified: false), modality, qualification).Error.ShouldBe(ProfessionalErrors.NotVerified);
        TherapyRules.EnsureCanProvideCare(practitioner, Acupuncture(enabled: false), qualification).Error.ShouldBe(TherapyErrors.ModalityDisabled);
        TherapyRules.EnsureCanProvideCare(practitioner, modality, null).Error.ShouldBe(TherapyErrors.NotQualified);

        qualification.Revoke(Guid.CreateVersion7(), "Cancelado", TestData.Now);
        TherapyRules.EnsureCanProvideCare(practitioner, modality, qualification).Error.ShouldBe(TherapyErrors.NotQualified);
    }

    [Fact]
    public void A_qualification_is_not_verified_for_a_council_outside_the_modalitys_scope()
    {
        var modality = Acupuncture();
        var nutritionist = Practitioner("CRN");
        var qualification = PractitionerQualification.Submit(nutritionist, modality, "Curso", "Instituto", null, null, TestData.Now).Value;

        qualification.Verify(nutritionist, modality, Guid.CreateVersion7(), null, TestData.Now).Error.ShouldBe(TherapyErrors.CouncilNotAccepted);
        qualification.Status.ShouldBe(QualificationStatus.Pending);
    }

    [Fact]
    public void Completion_requires_the_mandatory_fields_then_a_consent_in_force_on_the_current_version()
    {
        var (session, practitioner, modality, qualification, template) = Scheduled();
        var consent = TherapyConsent.Decide(session.ClientId, session.TenantId, practitioner, template, ConsentDecision.Granted, TestData.Now);

        session.Complete(practitioner, modality, qualification, consent, template, TestData.Now).Error.Code.ShouldBe("TherapySession.MissingDocumentation");
        session.Document(practitioner.Id, FullRecord, TestData.Now).IsSuccess.ShouldBeTrue();

        var refused = TherapyConsent.Decide(session.ClientId, session.TenantId, practitioner, template, ConsentDecision.Refused, TestData.Now);
        session.Complete(practitioner, modality, qualification, refused, template, TestData.Now).Error.ShouldBe(TherapySessionErrors.ConsentRequired);

        var newer = ConsentTemplate.Publish(modality, template.Version, "Termo", "Texto", ["Dor local", "Hematoma"], Guid.CreateVersion7(), TestData.Now).Value;
        session.Complete(practitioner, modality, qualification, consent, newer, TestData.Now).Error.ShouldBe(TherapySessionErrors.ConsentRequired);

        session.Complete(practitioner, modality, qualification, consent, template, TestData.Now).IsSuccess.ShouldBeTrue();
        (session.Status, session.ConsentId).ShouldBe((TherapySessionStatus.Completed, consent.Id));
    }

    [Fact]
    public void A_withdrawn_consent_no_longer_allows_completion_and_cannot_be_withdrawn_twice()
    {
        var (session, practitioner, modality, qualification, template) = Scheduled();
        session.Document(practitioner.Id, FullRecord, TestData.Now);
        var consent = TherapyConsent.Decide(session.ClientId, session.TenantId, practitioner, template, ConsentDecision.Granted, TestData.Now);

        consent.Withdraw(Guid.CreateVersion7(), null, TestData.Now).Error.ShouldBe(ConsentErrors.NotFound);
        consent.Withdraw(session.ClientId, "Desisti", TestData.Now).IsSuccess.ShouldBeTrue();
        consent.Withdraw(session.ClientId, "Desisti", TestData.Now).Error.ShouldBe(ConsentErrors.NotActive);

        session.Complete(practitioner, modality, qualification, consent, template, TestData.Now).Error.ShouldBe(TherapySessionErrors.ConsentRequired);
        consent.DisclosedRisks.ShouldBe(["Dor local"]);
    }

    [Fact]
    public void Modalities_without_consent_complete_on_documentation_alone_but_never_before_they_start()
    {
        var (session, practitioner, modality, qualification, _) = Scheduled(consent: false);
        session.Document(practitioner.Id, FullRecord, TestData.Now);

        session.Reschedule(practitioner.Id, TestData.Now.AddHours(2), TestData.Now).IsSuccess.ShouldBeTrue();
        session.Complete(practitioner, modality, qualification, null, null, TestData.Now).Error.ShouldBe(TherapySessionErrors.NotStarted);
        session.Complete(practitioner, modality, qualification, null, null, TestData.Now.AddHours(2)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Completed_records_are_corrected_with_history_and_cannot_lose_mandatory_fields()
    {
        var (session, practitioner, modality, qualification, template) = Scheduled();
        session.Document(practitioner.Id, FullRecord, TestData.Now);
        session.Complete(practitioner, modality, qualification,
            TherapyConsent.Decide(session.ClientId, session.TenantId, practitioner, template, ConsentDecision.Granted, TestData.Now), template, TestData.Now);

        session.Document(practitioner.Id, FullRecord, TestData.Now).Error.ShouldBe(TherapySessionErrors.NotScheduled);
        session.CorrectRecord(practitioner.Id, modality, FullRecord with { AdverseEvents = null }, "x", TestData.Now).Error.Code.ShouldBe("TherapySession.MissingDocumentation");
        session.CorrectRecord(practitioner.Id, modality, FullRecord with { AdverseEvents = "Pequeno hematoma" }, " ", TestData.Now)
            .Error.ShouldBe(TherapySessionErrors.CorrectionReasonRequired);
        session.CorrectRecord(practitioner.Id, modality, FullRecord with { AdverseEvents = "Pequeno hematoma" }, "Esqueci de registrar", TestData.Now)
            .IsSuccess.ShouldBeTrue();

        session.Record!.AdverseEvents.ShouldBe("Pequeno hematoma");
        session.Corrections.ShouldHaveSingleItem().Previous.AdverseEvents.ShouldBe("Nenhum");
    }

    [Fact]
    public void Cancellation_is_by_either_participant_only_and_completed_sessions_are_never_cancelled()
    {
        var (session, practitioner, _, _, _) = Scheduled();

        session.Cancel(Guid.CreateVersion7(), null, TestData.Now).Error.ShouldBe(TherapySessionErrors.NotFound);
        session.Cancel(session.ClientId, "Viagem", TestData.Now).IsSuccess.ShouldBeTrue();
        session.Cancel(practitioner.UserId, null, TestData.Now).IsSuccess.ShouldBeTrue();
        session.DomainEvents.OfType<TherapySessionCancelled>().ShouldHaveSingleItem().CancelledByClient.ShouldBeTrue();
        session.ReportProgress(session.ClientId, 3, null, TestData.Now).Error.ShouldBe(TherapySessionErrors.NotCompleted);
    }

    [Fact]
    public void Discharge_or_referral_closes_a_care_plan()
    {
        var practitioner = Practitioner();
        var modality = Acupuncture();
        var plan = TherapyCarePlan.Start(
            TestData.TenantId, Guid.CreateVersion7(), practitioner, modality, Qualified(practitioner, modality), "Reduzir dor", 6, "Semanal",
            new DateOnly(2026, 11, 1), TestData.Now).Value;

        plan.Review(practitioner.Id, CarePlanReviewOutcome.Continue, " ", null, null, null, null, TestData.Now).Error.ShouldBe(CarePlanErrors.NotesRequired);
        plan.Review(practitioner.Id, CarePlanReviewOutcome.Continue, "Evoluindo", null, null, null, new DateOnly(2026, 12, 1), TestData.Now).IsSuccess.ShouldBeTrue();
        plan.Review(practitioner.Id, CarePlanReviewOutcome.Discharge, "Objetivo atingido", null, null, null, null, TestData.Now).IsSuccess.ShouldBeTrue();

        plan.Status.ShouldBe(CarePlanStatus.Closed);
        plan.Review(practitioner.Id, CarePlanReviewOutcome.Continue, "x", null, null, null, null, TestData.Now).Error.ShouldBe(CarePlanErrors.Closed);
        plan.Reviews.Count.ShouldBe(2);
    }
}
