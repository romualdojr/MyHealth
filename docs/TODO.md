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
- [x] Who pays MyHealth: organizations and solo practices, priced per module (2.9).
- [ ] Real module prices, trial length, due and grace days (2.9 B1–B3) — placeholders are seeded.
- [ ] Payment provider (Pix + card; e.g. Asaas, Pagar.me, Mercado Pago, Stripe) and NF-e/NFS-e issuing (2.9 B8).
- [ ] Will professionals charge their own clients through MyHealth (RF-47 for clients)?
- [ ] AI provider for the assistant and its data processing agreement; review of the assistant terms text by legal (2.10 C1).
- [ ] Who films or licenses the exercise library videos (2.10 C4).
- [ ] Wellbeing scales (sleep, energy 1–5) and whether to add mood or stress (2.10 C6).
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
- [x] Tenant suspension through platform billing (per-module subscriptions, invoices, fake payment provider, back office).
- [ ] Per-tenant settings.

## Client experience — follow-ups (API side built, 2.10)

- [x] Exercise media (loops per angle, full video with captions and chapters) in the client plan; body checks; wellbeing check-ins and sharing; rest-time prompts; assistant consent, messages, confirm/discard; fake and Claude models.
- [ ] Media upload and CDN (signed uploads, transcoding to adaptive streaming, thumbnails) with tenant checks for trainers' videos (RF-27).
- [ ] Notification quick actions ("Segui o plano" from the lock screen) and reminders ("Lembrar às 19h") — with RF-07 and real push.
- [ ] Assistant: streaming replies, speech-to-text on the phone, evaluation set of Portuguese phrases before enabling Claude, cost limits per tenant.
- [ ] Assistant as a paid module, if wanted (billing has per-module prices).
- [ ] Back office: library media management screen.

## Platform billing — follow-ups

- [ ] Real `IPaymentProvider` (charge creation, webhook signature, refunds/chargebacks) and the provider's sandbox in staging.
- [ ] Self-service plan changes for tenant administrators (today operators change modules), with proration if wanted.
- [ ] Card / recurring payments; dunning e-mails before the due date.
- [ ] Review queue for `amount-mismatch` payment events in the back office.
- [ ] Back office behind VPN or IP allow list in production; operator action audit view for billing.
- [ ] Fiscal documents (NF-e/NFS-e) once the provider is chosen.
- [ ] PostgreSQL row-level security as defence in depth behind the application checks.
- [ ] Encryption at rest and tested backup/restore (RNF-02, RNF-05).

## Phase 4 — Clients and MVP release

- [x] Web dashboard foundation (Blazor): sign-in with 2FA, token refresh, organization switcher, platform admin console (RF-50), organization members and audit, trainer dashboard, contract tests.
- [x] Back office (separate Blazor app for operators) with the platform admin console and billing; shared `MyHealth.Web.Common` library.
- [x] Web: trainer workflow — clients (invite), plan editor (sessions, exercises, sets, aerobic), publishing with change description, version history, copy, end, review acknowledgement, exercise library, workouts with feedback replies ([TRAINER_WORKSPACE_PHASES.md](TRAINER_WORKSPACE_PHASES.md) phase 1).
- [ ] Web: trainer heart-rate views; plan templates without a client; drag and drop in the editor.
- [ ] Web: dietitian workflow (assessments, meal plan editor, food diary with comments; see [NUTRITION_WORKSPACE_PHASES.md](NUTRITION_WORKSPACE_PHASES.md)) and therapist workflow (services, schedule, session record, consent status, care plans).
- [ ] Web: invitation flows (clients and organizations), professional profile and onboarding, QR code for 2FA setup.
- [x] Web: end-to-end browser tests (Playwright + Edge, `tests/MyHealth.E2ETests`): sign-in, every page of both apps, plan editor, AI coach, feedback reply, exercise library.
- [x] Web: mobile first for iPhone and Android (tab bar, touch sizes, card lists, bottom sheets, PWA install, reconnection), with phone e2e tests.
- [ ] Web: accessibility review (WCAG 2.2 AA); e2e for the 3D upload with a real video and for the organization billing (Pix) flow; test on real devices (Safari iOS, Chrome Android) and push notifications for the installed app.
- [ ] iOS app integrating `docs/initial_requirements/ios` (needs a Mac with Xcode); Android app.
- [ ] Accessibility (RNF-07), performance targets (RNF-06), on-device acceptance tests 10 and 12.

## Trainer workspace ([TRAINER_WORKSPACE_PHASES.md](TRAINER_WORKSPACE_PHASES.md))

- [x] Web plan builder (phase 1).
- [x] AI coach for trainers: `AiCoaching` sharing grant, pseudonymised context, rule-based and Claude models, server-side guard rails, suggestions applied to the draft only, web panel in the plan editor.
- [x] WhatsApp behind `IWhatsAppProvider` (fake): opt-in with a code, opt-out in the app or by replying SAIR, outbox channel with generic templates, click-to-chat for professionals, data export.
- [x] 3D exercise videos: `IMotionCaptureProvider` (DeepMotion client + fake), `MotionCaptureJob`, worker with lease and back-off, source video deleted after processing, glb model + rendered loops accepted into the exercise (`MediaKind.Model3D`), web upload panel with a 3D preview.
- [x] Minimal media storage (`IMediaStorage`, local disk; public files served at `/media/` in development).
- [ ] **(decide)** WhatsApp provider (Meta Cloud API or a BSP: Twilio, Zenvia, Gupshup, Take Blip), business number, and approval of the three templates (`myhealth_codigo`, `myhealth_aviso`, `myhealth_saida`); then implement `IWhatsAppProvider` for it.
- [ ] **(decide)** DeepMotion contract (plan, credits per second of video, DPA); validate the API shapes in `DeepMotionMotionCaptureProvider` against the account's sandbox; choose the character model and whether more camera angles are rendered.
- [ ] **(legal)** AI coach: DPA with the AI provider, client-facing text for the "Análise por IA" sharing option, evaluation set of real cases before enabling Claude (`Coach:Provider=Anthropic`).
- [ ] Production media storage: bucket + CDN implementation of `IMediaStorage`, signed uploads straight from the browser/phone (today the video passes through the web app and API), transcoding and thumbnails (RF-27).
- [ ] Phone apps: render `Model3D` media (glTF; USDZ conversion for iOS Quick Look if wanted), WhatsApp opt-in screen, "Análise por IA" sharing toggle.
- [ ] 3D for the curated library (operators' back office) and a notification to the trainer when a conversion finishes.
- [x] Trainer chat ("Assistente IA"): all students with summaries, chat over the students who allow AI analysis (pseudonymised), suggestions to evolve exercises applied to each draft.
- [ ] AI coach and chat as a paid module, per-tenant daily limit for chat messages and cost reporting; first-draft plan generation from goal/level/equipment.
- [ ] Sign-in rate limit: the API limits logins per IP, and every dashboard sign-in comes from the web app's address — forward the user's IP (trusted proxy) or limit per account before production.

## Phase 5 — P1 features

- [ ] RF-08 messaging, RF-09 calendar, RF-14 extra workouts, RF-15 periodisation, RF-16 offline, RF-22/RF-44 heart-rate zones,
      RF-26 goals, RF-27 files, RF-31/32 nutrition goals and allergy warnings, RF-37 other modalities, RF-47/49 client billing and reports (RF-48 idempotent payment webhooks are done for platform billing).

## Phase 6 — P2

- [ ] Advanced HealthKit analysis, only after data-quality validation and the Anvisa/RNF-10 assessment.
