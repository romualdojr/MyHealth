# MyHealth API — Architecture

## Layers (Clean Architecture)

```
MyHealth.ApiService      Minimal-API endpoints, auth, ProblemDetails, OpenAPI      ─┐
MyHealth.Infrastructure  EF Core/PostgreSQL, Identity, repositories, audit, UoW     │ depend
MyHealth.Application     CQRS commands/queries + handlers, validation, access rules │ inwards
MyHealth.Domain          Aggregates, value objects, domain events, Result/Error     ◄┘
MyHealth.AppHost         Aspire orchestration (PostgreSQL + pgAdmin + API + web dashboard + back office)
MyHealth.Web             Blazor web dashboard (professionals, organization admins) — an API client only
MyHealth.Backoffice      Blazor back office for MyHealth's own operators (billing, verification, catalogs) — an API client only
MyHealth.Web.Common      Razor class library shared by both: API client and session, sign-in, account pages, components
```

The dependency rule is enforced by `tests/MyHealth.ArchitectureTests`.

## Bounded contexts

| Context | Aggregates | Notes |
|---|---|---|
| Tenants | `Tenant`, `TenantMembership` | The service organization (clinic, team or solo practice) that owns care records. Memberships hold the disciplines a person practises there and the tenant-admin flag. |
| Users | `UserProfile` | Id = Identity user id. Holds the IANA time zone used for attendance. |
| Professionals | `ProfessionalProfile` | One per user per discipline; admin verification; registration change resets verification. |
| Care relationships | `CareRelationship` (+ `DataSharingGrant`) | pending → active → ended. Per-category grants with full history. |
| Training | `Exercise`, `TrainingPlan`, `TrainingPlanVersion`, `WorkoutSession` | Draft is editable; publishing creates an immutable version (jsonb). Sessions reference the version. |
| Health data | `HealthConnection`, `WorkoutHeartRate` | Apple Health connect/pause/revoke; heart-rate ingestion with dedup, finalisation and reconciliation. |
| Notifications | `Notification`, `PushDevice` | Created by domain-event handlers in the same transaction; each row is also its own outbox entry for push/e-mail delivery. |
| Assessments *(Phase 2)* | `HealthHistory`, `PhysicalAssessment` | Health history is the client's own (no tenant), versioned per submission and reviewed by professionals. Physical assessments belong to the trainer's tenant; every measurement carries metric, method and unit. |
| Nutrition *(Phase 2)* | `NutritionAssessment`, `MealPlan`, `MealPlanVersion`, `FoodDiaryEntry` | Mirrors training: editable draft, immutable numbered versions. Diary entries are addressed to one dietitian relationship (or private) and never visible to trainers. |
| Privacy *(Phase 2)* | `PrivacyRequest`, `RetentionPolicy` | Data exports, correction and deletion requests (LGPD), and the retention catalogue. |
| Therapies *(Phase 3)* | `TherapyModality`, `PractitionerQualification`, `TherapyService`, `ConsentTemplate`, `TherapyConsent`, `TherapySession`, `TherapyCarePlan` | Modalities (global) carry the legal configuration — accepted councils, required record fields, consent rule — and stay disabled until an operator enables them with a legal-review reference. Qualifications are global per practitioner and modality. Services, consents, sessions and care plans belong to a tenant. `TherapyRules.EnsureCanProvideCare` (verified profile, enabled modality, council in scope, verified qualification) is re-checked on every care action; completion also needs the mandatory fields and a consent in force on the current consent version. |

## Key decisions

- **CQRS without a mediator library.** Endpoints inject `ICommandHandler<,>` / `IQueryHandler<,>` directly; Scrutor registers handlers and wraps them in validation and logging decorators. (MediatR is now commercially licensed and adds little here.)
- **Write side:** repositories per aggregate + `IUnitOfWork`. **Read side:** `IReadDbContext` (no-tracking `IQueryable`) projected straight to response records.
- **Result pattern** for expected failures (`Result`, `Error`, `ValidationError`) → RFC 9457 problem details. Exceptions are only for bugs; concurrency/unique violations map to 409 so phones retry.
- **Domain events** are dispatched by the unit of work *before* commit, so side effects (notifications, review flags) are atomic with the change.
- **Notification outbox:** the `notifications` row is the outbox entry. `NotificationDispatcher` (a hosted service, every 15 s) claims due rows with `FOR UPDATE SKIP LOCKED` and a 5-minute lease, saves each outcome right after sending and stops starting sends a minute before the lease ends (so another instance never re-sends), pushes to every registered device or, with none, sends an e-mail (one device succeeding counts as delivered), and retries with back-off (1, 4, 16, 64, 256 min; 6 attempts). Messages are generic Portuguese texts plus a deep link; never clinical content. `IPushSender`/`INotificationEmailSender` are logging placeholders until providers are chosen (§8). Disable with `Notifications:DispatcherEnabled=false`. A push installation moves to another account only when the caller presents that phone's current token.
- **Idempotency by design:** client-generated ids for workout sessions and set logs; heart-rate `PUT` is a snapshot upsert with a DB unique index on `(workout, HealthKit sample UUID)`; optimistic concurrency via PostgreSQL `xmin`.
- **Data isolation:** every professional action goes through `ICareAccess` (active membership in the selected tenant including the discipline → own profile for the discipline → verified → active relationship *in that tenant* → active grant). Resources outside the caller's scope return 404, not 403, to avoid leaking existence. See *Multi-tenancy* below.
- **Privacy:** request payloads are never logged; audit entries hold metadata only (actor, action, resource, subject). Notifications contain pointers, not clinical content.
- **Identity:** ASP.NET Core Identity API endpoints (`/api/v1/auth/*`) with bearer tokens, lockout, e-mail confirmation and 2FA endpoints. The domain never sees credentials.
- **MFA enforcement (RNF-02):** acting for a tenant (any `X-Tenant-Id` request: professionals and tenant administrators) and every `/admin` endpoint require an account with two-factor enabled; otherwise `Security.TwoFactorRequired` (403). The check reads the account's `TwoFactorEnabled` flag (Identity then refuses sign-in without the second factor, and refreshed tokens drop the `amr` claim). Client views never require it. `Security:RequireTwoFactorForProfessionals` is `false` only in `appsettings.Development.json`.
- **Data export (RNF-04):** `POST /me/data-exports` records the request; the JSON package is assembled at download time (7 days), so it is always current. It contains only records whose subject is the caller — never the clients of a professional who exports their own data, never drafts — each with organization, responsible professional and method. Request and every delivery are audited; operators see that an export exists, never its contents.

## Multi-tenancy (§4.1.1, MT-01 → MT-10)

Shared database, shared schema, a `tenant_id` column on every tenant-owned row. Accounts are global; a person works
inside a tenant through a `TenantMembership`.

| Concern | Decision |
|---|---|
| What is global | `UserProfile`, `ProfessionalProfile` (a council registration belongs to the person, so it is verified once), `HealthConnection` (the client's own Apple Health consent), `HealthHistory` (the client's own statement; each review records its tenant), `PrivacyRequest`, `PushDevice`, `TherapyModality`, `ConsentTemplate`, `PractitionerQualification`. |
| What carries `tenant_id` | `CareRelationship`, `TrainingPlan`, `TrainingPlanVersion`, `WorkoutSession`, `WorkoutHeartRate` (inherited from the session, MT-06), `PhysicalAssessment`, `NutritionAssessment`, `MealPlan`, `MealPlanVersion`, `FoodDiaryEntry` (`null` = private, otherwise the tenant of the plan or relationship it was logged for), `TherapyService`, `TherapyConsent`, `TherapySession`, `TherapyCarePlan`, custom `Exercise` (`null` = curated library), `Notification` (`null` for account notices), `AuditEntry` (`null` for platform actions). Foreign keys to `tenants` everywhere except audit, which must accept denied attempts naming unknown tenants. |
| Selecting a tenant | The client sends `X-Tenant-Id`. `ITenantContext` honours it only for an **active membership** of the caller (MT-04); otherwise `Tenant.NotFound` (404) and a `deny` audit entry. Professional endpoints without the header return `Tenant.Required` (400). It is resolved once per request and nothing is cached server-side between requests, so switching tenants is just sending another header (the apps must drop their own tenant-scoped caches, MT-07). |
| Client view | `/me/*` and the client's workout/heart-rate endpoints need no header and span every tenant (MT-10). Records are labelled with `tenantId`/`organizationName` and the responsible professional. Client writes derive the tenant from the resource (plan → session → heart rate), never from input. |
| Professional view | Every query and command filters by the active tenant **and** the caller's profile. A known id from another tenant gives 404 and is audited as `deny` (MT-09); `ICareAccess` and `TrainerPlanLoader` are the choke points. Notifications with audience `Professional` are only listed in their tenant's context. |
| Roles | *Tenant administrator* (membership flag): invites/revokes members and reads the tenant's audit trail — no clinical access unless they also practise and have a relationship (MT-05). *Platform administrator* (Identity role): verifies credentials, lists tenants, reads audit across tenants — no access inside any tenant. |
| Leaving a tenant | Revoking a membership raises `TenantMembershipRevoked`; in the same transaction the person's pending/active relationships in that tenant end (grants revoked). Records stay with the tenant (MT-08). The last administrator cannot be removed. |
| Existing data | Migration `MultiTenancy` gives every pre-existing professional a solo-practice tenant (id = user id) with an administrator membership, and attaches their records to it. |

Not yet implemented (needed as the related features arrive): client reassignment/transfer between professionals of a
tenant (RF-05), tenant suspension for billing, per-tenant settings, and the same tenant checks for files (RF-27, needed for
assessment and diary photos) and future background jobs (MT-07/MT-09). Exports are scoped to the data subject; the
notification dispatcher only delivers rows already tenant-labelled. PostgreSQL row-level security would add defense in depth
behind the application checks.

## Heart-rate contract (server side of `docs/initial_requirements/ios`)

`PUT /api/v1/workout-sessions/{id}/heart-rate` accepts the iOS `WorkoutHeartRateReport` unchanged.

| Report | Behaviour |
|---|---|
| `endedAt = null` (in progress) | Additive. New samples accepted, identical ones counted as duplicates, changed ones recorded as corrections. |
| `endedAt` set, first time | **Final.** Samples missing from the snapshot are marked *removed by source* (kept for history). Summary recalculated. |
| `endedAt` set, again | **Reconciliation** (HK-08): late samples / deletions applied, revision and `lastRevisedAt` bumped. |
| `endedAt = null` after final | **Ignored as stale** (HK-07): cannot revert the completed state. |

Samples must be 20–300 bpm, inside the server's session interval, and outside periods when the client had paused or revoked import. A HealthKit sample is stored once per client (unique index on `client_id, external_id`). Phone-computed mean/max are ignored; the API stores an arithmetic and a time-weighted mean, each with its method name, plus a count of self-reported (`wasUserEntered`) samples. Uploads are refused while Apple Health is paused or revoked, and reconciliation is accepted for 30 days after the workout ends (`GET /me/health-connections/apple-health/reconciliation` lists what to re-query). Professionals read heart rate only through an active relationship with an active `HeartRate` grant while the client has not revoked Apple Health, only for workouts of that relationship's tenant, and every read is audited.

Saving workouts *to* Apple Health is a separate opt-in (`PUT /me/health-connections/apple-health/workout-saving`); the phone reports the saved HKWorkout with `PUT /workout-sessions/{id}/apple-health-workout`, and only one per session is accepted. The full iPhone contract is in `docs/initial_requirements/ios/README.md`.

## Web dashboard and back office (Phase 4, started)

`MyHealth.Web` (professionals and organization administrators) and `MyHealth.Backoffice` (MyHealth's operators) are
Blazor (Interactive Server, no prerendering) apps that talk to the API only over HTTP, like the phone apps will. Neither
has a database. Both use `MyHealth.Web.Common`, which keeps copies of the response shapes they need
(`Api/ApiModels.cs`); `WebDashboardContractTests` reads every one of them from the real API, so drift fails the build.
The two apps have separate cookies; the back office refuses to sign in anyone without the operator role. It is a
separate deployable so it can sit behind a VPN or IP allow list in production.

| Concern | Decision |
|---|---|
| Sign-in | The static `/login` page calls `/auth/login` (bearer tokens, two-factor step included) and signs the person into the dashboard's own HTTP-only, SameSite=Strict cookie. The access and refresh tokens travel only inside that encrypted cookie and server memory, never to browser JavaScript. `ApiSession` refreshes the access token transparently; a failed refresh means signing in again. Return URLs are sanitized to local paths. |
| Organization | `WorkspaceState` loads `/me` and keeps the selected organization per circuit (remembered in protected browser storage); `MyHealthApiClient` sends it as `X-Tenant-Id`. Pages reload on a switch (MT-07); the API re-checks the membership on every call. |
| Authorization | Pages declare `[Authorize]`; platform pages require the operator role (taken from `/me`, or probed for operators without a personal profile). The API remains the authority: the UI only hides what the API would refuse. |
| Phones (mobile first) | The dashboard is built for iPhone and Android first: a bottom tab bar (Início, Clientes, Assistente, Biblioteca, Mais) with the sidebar as a drawer behind "Mais" (organization and account inside it), safe-area insets (notch, home indicator), 44 px touch targets and 16 px field text below `sm` (no iOS zoom), tables that become card lists below `md`, bottom sheets for in-place panels (`Ui.Sheet`), sideways-scrolling chip rows. Installable as an app (PWA: `manifest.webmanifest`, icons, a service worker that only serves `offline.html` when there is no connection). Phones drop the Blazor connection in the background: disconnected sessions are kept 20 minutes and `ReconnectNotice` shows a calm "Reconectando…" or a reload button. `tests/MyHealth.E2ETests/PhoneTests.cs` (iPhone 14 and Pixel 7 emulation) fails on sideways scrolling, targets under 44 px or fields under 16 px, and saves screenshots. |
| Styling | Tailwind CSS v4 only (no Bootstrap, no component CSS, no scoped `.razor.css`). `MyHealth.Web.Common/Styles/tailwind.css` is built into `wwwroot/css/app.css` by an MSBuild step (`npm run build:css`, Node needed only to change styles; the generated file is committed). Recurring class combinations (buttons, fields, cards, tables, badges, alerts, navigation) are C# constants in `MyHealth.Web.Common/Ui.cs`; both apps share the frame `AppShell` (sidebar drawer with a CSS-only toggle, top bar). |
| Screens so far | Both: sign-in, two-factor setup. Dashboard: home, organization members, audit, plan and billing (pay with Pix), billing banner when past due or suspended; trainer workspace: clients (invite), client (plans, new plan, copy, workouts and feedback replies), plan editor (details, strength and aerobic sessions, exercise picker, sets by repetitions or time, rest, instructions, save draft, publish with change description, versions, review acknowledgement, end; unsaved-changes guard), exercise library (search, the organization's own exercises). The editor's mutable draft (`MyHealth.Web.Common.Training.PlanDraft`) produces the exact request bodies, and the contract tests use it. Back office: billing overview (MRR, open/overdue, recent payments, run cycle), subscriptions, subscription detail (modules, trial, invoice now, suspend/reactivate/cancel, simulate/record payment, void), module prices; professional verification, therapy qualifications, therapy modalities (legal gate, consent versions), LGPD requests, retention, audit, organizations, operators. Portuguese (RNF-07). |

## Platform billing (who pays MyHealth)

Organizations and solo practices — tenants — pay MyHealth, monthly and in advance, for the **modules** they use:
Training (personal trainers), Nutrition (dietitians), Therapies (therapy practitioners). Clients never pay MyHealth.

| Concern | Decision |
|---|---|
| Model | `ModulePrice` (per module, effective-dated, history kept), `TenantSubscription` (one per tenant: modules, status, trial end, next period, due/grace days, status history), `Invoice` (one per period, lines snapshotted at the prices in force when issued, owned provider charge), `PaymentEvent` (every provider event received, unique per provider + event id). |
| Life cycle | A new tenant starts a 30-day **trial with every module**; its disciplines become its paid modules. The billing cycle (`BillingCycleService`, daily, also runnable from the back office) issues the invoice for each period that has started, then reconciles: an invoice past its due date (issue + 5 days) makes the subscription **past due** (a warning), past the 10-day grace period **suspended**; paying everything overdue makes it **active** again. An operator's suspension is lifted only by an operator. **Cancelled** is final. |
| Enforcement | `ITenantContext` loads the subscription with the membership; `ICareAccess` refuses professional work with `Tenant.Suspended` (suspended/cancelled) or `Tenant.ModuleNotSubscribed` (after the trial, a discipline whose module is not paid). Tenant administration and `/tenant/billing` keep working, so a suspended tenant can still pay; clients keep access to their own data. A tenant without a subscription row counts as suspended (the migration backfills a trial for existing tenants). |
| Payments | `IPaymentProvider` creates a charge (Pix copy-paste and/or checkout URL) and parses signed webhooks. The only implementation today is `FakePaymentProvider` (HMAC-SHA256 in `X-Fake-Signature`, secret `Payments:Fake:WebhookSecret`); operators can "simulate payment", which signs a real webhook body and runs it through the same pipeline. Webhooks are idempotent (RF-48), and an amount different from the invoice is recorded as `amount-mismatch`, not applied. Operators can also record offline payments or void an invoice, with a note. |
| Notifications | Tenant administrators are notified of each new invoice and of past due / suspended / active / cancelled changes (outbox). |
| Endpoints | Operators: `/admin/billing/*` (summary, prices, subscriptions, actions, invoices, run cycle). Tenant administrators: `GET /tenant/billing`, `POST /tenant/billing/invoices/{id}/charge`. Provider: `POST /payments/webhooks` (anonymous, signature-checked). |

Prices are seeded as placeholders (Training R$ 99, Nutrition R$ 49, Therapies R$ 49) and are meant to be set in the back office.

## Client experience (less typing for the client)

What the client prototype needs from the API: exercise videos, small questions during rest, wellbeing check-ins, body
checks and an assistant that turns what the client says into records they confirm.

| Concern | Decision |
|---|---|
| Exercise media | `Exercise.Media` (jsonb): up to 8 items, each a `Loop` (muted, for the workout screen; one per camera angle), `FullVideo` (duration, captions, poster, chapters) or `Image`. URLs must be https on `Media:AllowedHosts` (default `media.myhealth.app`) — owned or licensed media only. Video requires the text description (`mediaCaption`) first, for accessibility. Changing media does not bump the exercise version. Operators manage library media (`PUT /admin/exercises/{id}/media`), trainers their own exercises (`PUT /exercises/{id}/media`). The client plan response lists every exercise of the plan with its media, so the phone can download them for offline use. Upload and the CDN itself are not built yet (RF-27). |
| Body checks | `WorkoutSession.BodyChecks` (jsonb): subject ("joelho direito"), answer (better / same / worse / new), details, reported to trainer. Allowed during the workout and up to 7 days after it; idempotent on a phone-generated id. Reporting raises `DiscomfortReported`, the same notification as the end-of-workout flag. Never interpreted. |
| Wellbeing | `WellbeingCheckIn`: sleep and energy, 1–5, one per local day (answering again updates), up to 7 days back. Owned by the client (no tenant); professionals read it only with the new `Wellbeing` sharing category. In the data export. |
| Prompts | `GET /me/prompts?workoutSessionId=` builds, from existing records, at most four questions in priority order: follow-up of a discomfort reported to this trainer in the last 14 days (only during a workout, once), the latest planned meal of the last 5 hours not yet in the diary, the daily wellbeing check-in, and feedback on a therapy session completed in the last 7 days. Each names who receives the answer (the meal one says the trainer never sees the diary) and the existing endpoint that answers it, so answering removes it. When to show them (one per rest, never in the last 15 s, after the heart rate drops) is the phone's decision; skipping is local. |
| Assistant consent | `AssistantConsent`, one per user: terms version (currently 1, text served by `GET /me/assistant`), the areas it may read (training, nutrition, therapies), history. A new terms version needs a new acceptance; withdrawing stops messages and confirmations. |
| Assistant messages | `POST /me/assistant/messages` (rate-limited per user, `RateLimiting:AssistantPerMinute`, default 20). The server builds a context from the consented areas only (today's plan and meals, recent workouts, last sets, upcoming therapy sessions) and calls `IAssistantModel` with fixed rules: no diagnosis, no plan changes, no consent on the client's behalf, Portuguese, only ids from the context. The model writes through tools (`log_meal`, `report_body_check`, `log_set`) that never run: each becomes an `AssistantProposal` (1 hour) returned as a card. Ids the model invents are dropped. Pain cards carry fixed safety text set by the server. Each message is audited as `assist` (no content). |
| Confirming | `POST /me/assistant/proposals/{id}/confirm` runs the same command the forms use (`SaveFoodDiaryEntry`, `RecordBodyCheck`, `LogSet`) as the client, so access checks, sharing and notifications are unchanged; the payload carries client-style ids, so confirming twice creates one record. `discard` closes it. Proposals older than a day are deleted on the user's next message. |
| Models | `FakeAssistantModel` (rule-based Portuguese: sets by voice, meals, pain, last load, next meal) is the default and is used in tests. `AnthropicAssistantModel` (Claude Messages API with tool use) with `Assistant:Provider=Anthropic`, `Assistant:Anthropic:ApiKey` (secret) and optional `Assistant:Anthropic:Model` (default `claude-sonnet-5`); failures return "unavailable" and the app falls back to the forms. |

Not built: notification quick actions and reminders ("Lembrar às 19h", lock-screen answers) wait for RF-07 reminders and
real push; meal photos wait for file storage.

## Trainer integrations: AI coach, WhatsApp, 3D exercise videos

Each integration sits behind an Application interface with a fake implementation that is the default, so development,
tests and demos never need the provider; the real one is selected by configuration.

| Concern | Decision |
|---|---|
| AI coach — consent | The client grants the new sharing category `AiCoaching` to the trainer (revocable like any grant). Without it nothing is sent (`Coach.SharingRequired`). Wellbeing is included only with the `Wellbeing` grant too; health history, nutrition and therapies never are. |
| AI coach — context | `CoachContextBuilder`: the plan draft, the last 6 weeks of workouts of this plan (sets logged vs prescribed, RPE, discomfort, comments trimmed), body checks reported to the trainer, adherence per week, and library alternatives (same muscle groups, available to the tenant). Pseudonymised: no name, e-mail or birth date. |
| AI coach — output | The model calls tools (`change_load`, `change_sets`, `change_repetitions`, `change_rest`, `swap_exercise`, `add_instruction`, `talk_to_client`). `CoachSuggestionPolicy` keeps only ids from the context, loads within ±15 % (rounded to 0.5 kg), ±2 sets, 1–50 repetitions, rest ≤ 600 s, swaps from the alternatives; after discomfort or a worse/new body check in the last 14 days it drops every progression, adds a fixed notice and a "talk to the client" card. Stored as `CoachAnalysis` (tenant-owned; suggestions in jsonb with their outcome). |
| AI coach — applying | `POST /coach-analyses/{id}/suggestions/{sid}/apply` changes the plan's **draft** only (`CoachSuggestionApplier`); the trainer still publishes. A suggestion whose exercise left the draft returns `Coach.PlanChanged`. Each analysis is audited as `assist` with the client as subject; per-user rate limit and `Coach:DailyAnalysesPerTenant` (default 100). |
| AI coach — models | `RuleBasedTrainingCoachModel` (default; progressive overload / deload / adherence rules) and `AnthropicTrainingCoachModel` (`Coach:Provider=Anthropic`, key `Coach:Anthropic:ApiKey` or the assistant's, model `Coach:Anthropic:Model`, default `claude-sonnet-5`). |
| Trainer chat | `/trainer/assistant` (web "Assistente IA"): every active student of the trainer with summaries computed from the records (adherence vs sessions expected since the plan started, last workout, average RPE, discomfort, feedback awaiting reply; no AI). The chat (`POST /trainer/assistant/messages`, history from the page, optional focused student) sends only students with the `AiCoaching` grant, at most 25, under aliases ("Aluno 1"): `StudentAliases` replaces names typed by the trainer before the model and turns aliases back into names after. Plan changes use the coach's tools and `CoachSuggestionPolicy`, are stored as `CoachAnalysis` per plan and applied with the same endpoints. Each included student is audited (`assist`, subject = the student). Models: `RuleBasedTrainerAssistantModel` (default) and `AnthropicTrainerAssistantModel` (`TrainerAssistant:Provider=Anthropic`). |
| WhatsApp — provider | `IWhatsAppProvider`: send a template, read a signed webhook, answer the subscription check. Only `FakeWhatsAppProvider` exists (logs template + masked number; numbers ending 0000 are "not on WhatsApp"; HMAC webhooks like the fake payments). Templates: `myhealth_codigo` (code), `myhealth_aviso` (title + body of the notification, generic), `myhealth_saida` (opt-out confirmation). |
| WhatsApp — opt-in | `WhatsAppContact` per user: E.164 number (Brazilian numbers without +55 accepted), 6-digit code (hashed, 10 min, 5 attempts, resend after 60 s), `Active` only after confirmation, history of changes, `AllowProfessionalContact`. Opt-out in the app or by replying SAIR/PARAR/STOP (`POST /whatsapp/webhooks`, anonymous, signature-checked). Included in the data export. |
| WhatsApp — use | The notification outbox also sends `myhealth_aviso` to an active contact (same generic text as push/e-mail; one channel delivered = delivered). Professionals of an active relationship get a `wa.me` link (`GET /care-relationships/{id}/whatsapp`, audited) only when the client allowed it — click-to-chat, nothing automatic. |
| 3D — flow | The trainer who created an exercise accepts the terms (version 1) and uploads a video (≤ 60 s, ≤ `MotionCapture:MaxUploadMegabytes`, default 200) to `POST /exercises/{id}/motion-captures`. The video is stored privately; `MotionCaptureProcessor` (hosted worker, lease per job, back-off on provider errors) submits it, polls, copies the results to public storage, deletes the source. The trainer reviews (3D viewer + videos) and accepts: the `glb` becomes `MediaKind.Model3D` and each rendered `mp4` a loop for its camera angle (replacing loops of the same angle). Monthly cap `MotionCapture:MonthlyJobsPerTenant` (default 30). |
| 3D — providers | `FakeMotionCaptureProvider` (default; returns the video as side/front/back renders and a placeholder glTF) and `DeepMotionMotionCaptureProvider` (`MotionCapture:Provider=DeepMotion`, `ApiBaseUrl`, `ClientId`, `ClientSecret`, optional `Model`): session auth, signed upload, `video2anim` with mp4/glb/fbx, status, download. Shapes to be validated against the account's sandbox. |
| Media storage | `IMediaStorage` with keys under `public/` (served by the media host) and `private/` (never served). `LocalMediaStorage` (`Media:LocalPath`, `Media:PublicBaseUrl`) for development, served at `/media/{key}` by the API; production needs a bucket + CDN implementation and direct signed uploads. |

## Running

```bash
dotnet run --project src/MyHealth.AppHost        # PostgreSQL + API + web dashboard (webfrontend) + back office (backoffice)
```

API reference (Development): `/scalar/v1`. Migrations are applied on startup in Development.

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/MyHealth.Infrastructure -s src/MyHealth.ApiService -o Persistence/Migrations
dotnet test                                      # unit + architecture + integration (integration needs Docker)
```
