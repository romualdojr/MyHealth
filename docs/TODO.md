# MyHealth — To-do

Forward-looking backlog. Details and reasoning live in [REQUIREMENTS_REVIEW_AND_PHASES.md](REQUIREMENTS_REVIEW_AND_PHASES.md)
(findings §2, phases §3, open decisions §4) and [ARCHITECTURE.md](ARCHITECTURE.md). Tick items off here as they land.

## Decisions needed (business / legal)

- [ ] **Therapies legal review** (blocks go-live of Phase 3, which is built): accepted councils for acupuncture under Law 15,345/2026, required record fields, consent wording and disclosed risks, re-consent on new versions (2.8 R1–R4).
- [ ] Health history visible to trainers only with the client's grant (2.7 N1) — confirm.
- [ ] Fields that count as a "significant" health-history change: limitations and injuries (2.7 N2) — confirm.
- [ ] Should the dietitian also authorize the trainer's nutrition summary, or is the client's grant enough (2.7 N7)?
- [ ] Retention period and legal basis per record type (§12.5, RNF-01) — the catalogue exists, values are empty.
- [ ] Push and e-mail providers (§8) — delivery uses logging placeholders until chosen.
- [ ] RPO/RTO targets (RNF-05); heart-rate upload cadence (§12.6).
- [ ] Commercial model and who invoices the client (§12.1/12.2) — needed for billing and tenant suspension.
- [ ] Onboarding: should a solo practice be created automatically for independent professionals (2.6)?
- [ ] Confirm the RF-45/RF-46 numbering gap.

## Phase 3 — Therapies (built; go-live waits for the legal review)

- [x] RF-33 service catalogue per modality; practitioner qualifications verified per modality before appointments or records.
- [x] RF-34 acupuncture record (concern/goal, assessment, contraindications and precautions, technique, points/regions, duration, adverse events, guidance).
- [x] RF-35 procedure-specific, versioned consent with refusal and withdrawal; no completion without required documentation.
- [x] RF-36 appointments, session history, client-reported progress, care-plan review, referral for medical care.
- [x] Acceptance tests 7 (therapy part) and 8.
- [ ] Go-live: operators configure acupuncture with legal counsel's answers (accepted councils, required fields, consent text and risks) and enable it with the review reference.
- [ ] In-person (paper) consent capture, if legal requires it.
- [ ] Appointment reminders and confirmation (with RF-09).
- [ ] RF-37: add other modalities through the same catalogue after their own legal review.

## Follow-ups from Phase 2

- [ ] Photos for physical assessments (RF-24) and diary entries (RF-30) — need file storage (RF-27) with tenant checks (MT-07/MT-09).
- [ ] Enforce retention policies (purge/anonymise job) once periods are decided.
- [ ] Apply approved correction/deletion requests through tooling instead of by hand.
- [ ] Real push (APNs/FCM) and e-mail senders behind `IPushSender` / `INotificationEmailSender`; drop invalid device tokens.
- [ ] RF-07 configurable reminders.
- [ ] Notify trainers when a client ends a relationship so their plans can be ended (open plans stay "published" today).

## Platform and multi-tenancy

- [ ] RF-05 client reassignment between professionals of a tenant, with a transfer audit trail.
- [ ] Tenant suspension (billing), per-tenant settings.
- [ ] PostgreSQL row-level security as defence in depth behind the application checks.
- [ ] Encryption at rest and tested backup/restore (RNF-02, RNF-05).

## Phase 4 — Clients and MVP release

- [ ] iOS app integrating `docs/initial_requirements/ios`; Android app; professional web dashboard.
- [ ] Accessibility (RNF-07), performance targets (RNF-06), on-device acceptance tests 10 and 12.

## Phase 5 — P1 features

- [ ] RF-08 messaging, RF-09 calendar, RF-14 extra workouts, RF-15 periodisation, RF-16 offline, RF-22/RF-44 heart-rate zones,
      RF-26 goals, RF-27 files, RF-31/32 nutrition goals and allergy warnings, RF-37 other modalities, RF-47/48/49 billing and reports.

## Phase 6 — P2

- [ ] Advanced HealthKit analysis, only after data-quality validation and the Anvisa/RNF-10 assessment.
