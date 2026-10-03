# Fitness Training and Health Tracking App Requirements

**Version:** 1.0 — September 30, 2026  
**Purpose:** Specify a product for personal trainers, clients, dietitians, and qualified integrative therapy practitioners, with training as the core workflow.  
**Status:** Proposed requirements pending business, legal, and technical review; not a final interface specification.

## 1. Reference Basis and Limits

The four supplied screenshots show MFIT Personal's **client area**: (1) a home screen with trainer, CREF registration, weekly attendance, and shortcuts to workouts, extras, assessments, progress, invoices, and files; (2) a list of training plans with dates, goals, and levels, plus an aerobic tab; (3) workouts within a plan, with execution history, progress, feedback, and workout access; and (4) a workout view with session start, exercises, videos, sets, load, and rest. These images establish workflow and information hierarchy. Third-party names, logos, photos, text, and demonstration media require original replacements or licenses.

MFIT's public website describes client management, health history questionnaires, assessments, extra workouts, a video library, plan cloning and periodization, feedback, progress charts, documents, payments, and workouts at any time. These are reference capabilities, **not** features already implemented in this product. Sources: [home page](https://www.mfitpersonal.com.br/), [client management](https://www.mfitpersonal.com.br/pages/gerenciamento-de-alunos.html), [workout prescription](https://www.mfitpersonal.com.br/pages/prescricao-de-treinos.html), [client features](https://www.mfitpersonal.com.br/pages/solucoes-para-alunos.html), [financial management](https://www.mfitpersonal.com.br/pages/gestao-financeira.html).

## 2. Product Vision

**Proposal:** Clients receive prescribed workouts, complete them, and track progress. With specific permission, they may share nutrition records, therapy sessions, and heart-rate data obtained through HealthKit with the relevant professionals. Each professional has a dedicated dashboard with history and clear actions.

**Roles:** Client, personal trainer, dietitian, integrative therapy/acupuncture practitioner, organization administrator, and restricted support operator. A user may have multiple professional relationships, but each action records the role used. In-person and remote services are supported.

**Product principle:** Training is the core. Nutrition and therapy are independent modules enabled by organization or plan, with minimal and explicit sharing across teams. HealthKit data supports wellness tracking; it does not diagnose conditions or replace professional assessment.

## 3. Core User Journeys

1. **Client onboarding:** Receive an invitation, create an account, accept applicable terms and notices, enter goals, availability, restrictions, and an initial questionnaire, and choose which professionals may see each data category.
2. **Prescription:** The trainer reviews health history and assessments, creates a time-bound plan with strength and aerobic sessions, and publishes a version for the client.
3. **Execution:** The client opens the plan, views exercises and media, starts a session, records sets, loads, repetitions, and time, uses a rest timer, pauses or resumes, and finishes with perceived exertion and feedback.
4. **Follow-up:** The client and trainer see actual attendance, adherence, load progression, and assessments; the trainer revises and republishes the prescription with version history.
5. **Nutrition:** A dietitian receives authorized access, assesses the client, prescribes a meal plan, and tracks adherence and progress; the client records meals, water, difficulties, and feedback.
6. **Therapies:** A qualified practitioner records an assessment, schedules a session, documents the technique, guidance, and progress; the client sees appointments and guidance and can report how they feel afterward.
7. **HealthKit:** On the iPhone, the client authorizes reading heart-rate data from Apple Health, monitors synchronization, and can revoke access. Professionals see only data the client has authorized for sharing.

## 4. Functional Requirements

### 4.1 Identity, Relationships, and Access

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-01 | P0 | Invitation-based registration or self-signup, account recovery, and email/phone verification. A client cannot access another client's data. |
| RF-02 | P0 | Client-professional relationship with `pending/active/ended` states; ending a relationship stops new access while retaining records for the applicable period. |
| RF-03 | P0 | Permissions by role and purpose. Trainers edit workouts; dietitians edit meal plans; practitioners edit their own care records; clients view their own data; administrators do not automatically receive clinical access. |
| RF-04 | P0 | Professional profile with public name, photo, practice area, licensing or registration where applicable, credentials, and verification status before publishing prescriptions or providing care. |
| RF-05 | P0 | Organizations and teams with multiple professionals and clients, invitations, reassignment, and a transfer audit trail. Each independent professional receives a dedicated organization (tenant). |

#### 4.1.1 Multi-Tenant Access Model

The **tenant is the service organization**, such as a clinic, training team, or an independent professional's practice. A person has one account identity and may hold memberships in multiple tenants. The same client may work with professionals from different tenants without creating duplicate login accounts. Tenant membership alone does not grant access to a client's care records; a current client-professional relationship and the appropriate purpose-specific permission are also required.

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| MT-01 | P0 | Create a tenant for every service organization, including a solo practice. Each tenant has its own professionals, client relationships, plans, billing settings, and configuration. |
| MT-02 | P0 | Keep account identity global, but store professional roles as tenant-scoped memberships. A professional who belongs to two tenants chooses an active tenant; every action records that tenant and membership. |
| MT-03 | P0 | A client may have relationships with several professionals in one or more tenants. Each relationship has a role, status, effective dates, and permitted data categories. Ending one relationship must not affect unrelated relationships. |
| MT-04 | P0 | Every workout plan, execution, assessment, meal plan, therapy record, invoice, file, and related audit event carries a tenant ID plus client and author IDs. The API resolves tenant context from authenticated membership and checks the resource's tenant ID on every read and write. Client-supplied tenant IDs alone never authorize access. |
| MT-05 | P0 | Professionals see only clients assigned to them within the active tenant and only data needed for their role and purpose. Tenant administrators can manage memberships and billing but do not receive clinical content by default. |
| MT-06 | P0 | Heart-rate samples associated with a workout inherit that workout's tenant context. HealthKit permission authorizes access on the client's iPhone; separate in-app sharing permission controls which tenant and professionals can see the imported data. A tenant cannot browse data from another tenant's workout. |
| MT-07 | P0 | Switching active tenants clears tenant-specific cached data and refreshes authorization context. Search, exports, notifications, files, background jobs, and API queries must enforce the same tenant boundary. |
| MT-08 | P0 | When a professional leaves a tenant, revoke membership and future access immediately. Existing tenant care records remain under the organization's retention policy, with authorship and access history preserved. |
| MT-09 | P0 | Automated isolation tests attempt cross-tenant reads and writes with known resource IDs, including direct API calls, file URLs, exports, and HealthKit workout uploads. All unauthorized attempts are rejected and audited without revealing the resource's content. |
| MT-10 | P0 | The client's own dashboard may combine their records from multiple tenants, labeling each record by organization and responsible professional. This client view does not grant any professional cross-tenant access or automatically share records between organizations. |

### 4.2 Home and Communication

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-06 | P0 | Client home screen with greeting, assigned professional, next session, seven-day attendance, and shortcuts to Workouts, Assessments, Progress, Nutrition, Therapies, Apple Health, and Files. A day without a planned workout is rest, not a missed session. |
| RF-07 | P0 | Notification center for published or changed plans, scheduled sessions, feedback replies, and configurable reminders. Each notification opens the correct content and can be marked as read. |
| RF-08 | P1 | In-app client-professional messages with authorized attachments; a WhatsApp shortcut requires an explicit user action and does not place sensitive data in the automatic message. |
| RF-09 | P1 | Calendar for in-person and remote sessions with confirmation, cancellation, rescheduling, time zones, and the provider's cancellation policy. |

### 4.3 Workouts — Prescription and Library

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-10 | P0 | The trainer creates a **training plan** with name, goal, level, start/end dates, suggested frequency, notes, and `draft/published/ended` status. A plan can be copied and adapted without changing its history. |
| RF-11 | P0 | Each plan contains ordered **sessions**, including aerobic sessions. Each session contains ordered exercises, sets with repetition or duration targets, suggested load, rest interval, instructions, and alternatives. |
| RF-12 | P0 | Searchable exercise library by name, muscle group, equipment, and purpose. Each entry includes a description, precautions, owned or licensed media, and version. Professionals may add their own exercises and videos. |
| RF-13 | P0 | Publishing creates an immutable version for sessions already started. Later changes show a date and description; clients can see which version they will perform. |
| RF-14 | P1 | One-off extra workout with an expiration date and reason, without automatically changing the main periodization. |
| RF-15 | P1 | Weekly and block planning, deload, and progression; reminders for plan expiration and review, without automatic load changes unless the trainer approves them. |
| RF-16 | P1 | Offline access to a published workout with selected media and a version/expiration notice; later synchronization preserves recorded activity. |

### 4.4 Workouts — Execution and Progress

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-17 | P0 | The plan screen lists sessions, execution count, last execution, progress, feedback, and a View Workout action. |
| RF-18 | P0 | A session starts in preview mode. Starting it records the time and allows logging completed sets, repetitions, load, duration, distance, difficulty or notes, and timed rest. Paused, resumed, and abandoned are distinct states. |
| RF-19 | P0 | Finishing requests simple feedback (perceived exertion, optional pain/discomfort, and comments) and creates an execution record. Attendance counts **completed** sessions, never merely opening a screen. |
| RF-20 | P0 | Progress by exercise and period: load, repetitions, volume, and personal best. Compare only equivalent measures and show the source and date of every record. |
| RF-21 | P0 | The trainer sees planned versus actual adherence, pending feedback, completed sessions, and reported signs that require contact; the trainer can reply and revise the prescription. |
| RF-22 | P1 | Aerobic prescriptions include activity, duration or distance, zone or perceived intensity, warm-up, and cool-down. Executions may include HealthKit heart-rate data when available. |

### 4.5 Assessments and Progress

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-23 | P0 | Client-editable and professional-reviewable health history with goals, exercise history, limitations, reported injuries, availability, equipment, and acknowledgment. Significant changes flag the prescription for review. |
| RF-24 | P0 | Physical assessment with date, assessor, method and units, selected measurements, optional photos, and notes. Do not compare different methods as if they measured the same thing. |
| RF-25 | P0 | My Progress combines attendance, assessments, exercise progression, and professionally defined goals. Charts show the period and missing data without silent estimates. |
| RF-26 | P1 | Process goals (workouts per week, hydration, self-reported sleep) and outcome goals (measurements), with manual review and history. |
| RF-27 | P1 | Client files, including reports, tests, and documents uploaded by the client or an authorized professional, with type, date, source, permissions, and deletion under the applicable policy. |

### 4.6 Nutrition

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-28 | P0 | A verified dietitian records a nutrition assessment, goals, reported restrictions and allergies, preferences, meal times, measurements, and follow-up. Food records are not automatically visible to the trainer. |
| RF-29 | P0 | Versioned meal plan with dates, meals and times, foods and portions, substitutions, notes, and author. Publishing or changing a plan notifies the client and preserves auditable previous versions. **Only a qualified dietitian may issue a dietary prescription.** |
| RF-30 | P0 | Client food diary with meal, time, optional text or photo, water, and plan adherence. Unplanned foods can be recorded without punitive language. The professional can comment and revise the plan. |
| RF-31 | P1 | Individualized nutrition goals and period-based follow-up. Nutrient calculations show their food table or source, units, and degree of approximation. Activity metrics do not automatically generate intake prescriptions. |
| RF-32 | P1 | Allergy and restriction warnings in the editor and client-visible plan. Supplement recommendations require dietitian review and consideration of relevant clinical conditions. |

### 4.7 Integrative Therapies, Including Acupuncture

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-33 | P0 | Service catalog by modality and qualified practitioner. Verify qualifications and permitted scope for each modality before enabling appointments or care records. |
| RF-34 | P0 | Acupuncture record with reported concern or goal, professional assessment, contraindications and precautions, technique, points or regions when relevant, duration, adverse events, and post-session guidance. |
| RF-35 | P0 | Procedure-specific consent with version, date, practitioner, disclosed risks, and refusal or withdrawal record. A session cannot be marked completed without the required documentation. |
| RF-36 | P0 | Appointment schedule and session history, client-reported progress, practitioner review of the care plan, and referral for medical care when warning signs arise. |
| RF-37 | P1 | Other practices (such as guided meditation or massage therapy) require their own scope, training, registration, and documentation settings after legal and clinical review of each modality. |

### 4.8 Apple Health/HealthKit and Heart Rate

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-38 | P0 | The **Apple Health** screen explains heart-rate access, purpose, receiving professionals, and period. Connect, pause, and revoke are separate actions; workouts remain usable without HealthKit access. |
| RF-39 | P0 | On iPhone, integrate exclusively with **Apple Health/HealthKit**. Request heart-rate read access only after an explicit client action. Availability depends on samples present in Apple Health and the user's authorization. |
| RF-40 | P0 | Import heart rate when available and authorized. Show bpm, time, source, last synchronization, and missing-data state. No other health metrics are included in this integration's scope. |
| RF-41 | P0 | Associate heart-rate samples with the session by time interval and source, with mean, maximum, and a progress chart. Send samples to the API during the workout and finalize at completion. Do not imply continuous measurement when samples are sparse; show gaps and differing sources. |
| RF-42 | P0 | Deduplicate imports by source identifier, type, and interval; preserve provenance and a history of source corrections or deletions. Reprocessing cannot duplicate sessions or metrics. |
| RF-43 | P0 | Sharing with trainers, dietitians, and practitioners is granular. Their dashboards distinguish self-reported, measured, and estimated data. Revocation stops new reads and sharing; previously received data follows the defined legal basis and retention policy. |
| RF-44 | P1 | Trainers may configure workout heart-rate zones with a documented method and individual review. Client alerts provide exercise guidance, not diagnosis or emergency monitoring; unexpected values should prompt pausing and appropriate professional assessment under a defined protocol. |

#### 4.8.1 Specific Apple HealthKit Integration

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| HK-01 | P0 | The iOS app requests **explicit authorization on the iPhone** to read heart rate from Apple Health before its first query. Request only the required type. If access is denied, the workout continues without heart rate and the app does not claim permission was granted. |
| HK-02 | P0 | Read available HealthKit heart-rate samples without filtering by source. Any sample stored in Apple Health may be included when the user has authorized access. |
| HK-03 | P0 | When a workout starts, query heart-rate samples and associate those within the session interval with the active workout. Store sample identifier, time, bpm, and HealthKit-reported source. |
| HK-04 | P0 | During the workout, update the timeline and send received samples to the API using a stable session ID. Show the latest sample time and latest synchronization time; do not promise continuous updates when a source delivers data late. |
| HK-05 | P0 | On completion, perform a final interval query, deduplicate samples, send a completion update to the API, and store points and summary: mean heart rate, maximum heart rate, period, and calculation method. The API verifies session ownership, accepts idempotent retries, and recalculates the summary from accepted points. |
| HK-06 | P0 | Save the completed workout to Apple Health **only** when the client enables this option and grants workout write permission on the iPhone. Write permission is separate from read permission. Do not rewrite imported heart-rate samples. Store an external session identifier to prevent duplicates. |
| HK-07 | P0 | Network failure cannot discard samples or workouts: persist pending uploads on the phone and retry with the same identifier. In-progress updates and completion must preserve logical order; a partial update cannot overwrite a completed state. |
| HK-08 | P1 | Reconcile samples that appear in Apple Health after completion because of delayed source synchronization, respecting the authorized period, provenance, and later source deletions. Show when the summary was last revised. |

**Product criterion:** HealthKit exposes samples stored in Apple Health, and their arrival depends on the source. “During the workout” means displaying what is available at that time, without promising continuous updates.

### 4.9 Plans, Billing, and Operations

| ID | Priority | Requirement and minimum acceptance criteria |
|---|---|---|
| RF-47 | P1 | Service and subscription catalog by professional or organization, including price, duration, scope, effective dates, renewal, and terms. |
| RF-48 | P1 | Invoices with status, due date, line items, and receipt; payments through a third-party payment service provider using Pix or cards, with idempotent webhook reconciliation. Do not store full card details. |
| RF-49 | P1 | Operational reports for active clients, adherence, visits, and revenue, restricted to the manager's scope and without unnecessary sensitive data. |
| RF-50 | P0 | Admin console to verify professionals, manage catalogs and permissions, handle LGPD requests, audit access, and configure retention. |

## 5. Cross-Cutting Business and Acceptance Rules

- **Attendance:** Count a completed session on the client's local date. Multiple workouts on one day may be logged, but the weekly card distinguishes active days from total sessions. A missed workout requires a plan for that day and an expired completion window; rest is not an absence.
- **History:** Prescriptions, plans, and assessments have an author, date, and version. Each execution points to the version used. Corrections preserve prior records and record the reason and responsible person.
- **Discomfort:** Reports of pain, dizziness, or illness notify the responsible professional and direct the client toward appropriate care when needed; they do not create an automatic diagnosis.
- **Client autonomy:** Clients can log workouts without connecting Apple Health, hide unnecessary data, and request data correction or export under the policy.
- **Professional boundaries:** Trainers cannot prescribe diets through the nutrition module; therapy practitioners cannot change workouts. Cross-role access requires explicit permission and purpose.
- **Sample reliability:** Missing, delayed, or low-quality indicators are labeled as such. Do not invent values to fill gaps or use the data for automated clinical assessment.

## 6. Summary Access Matrix

| Data/action | Client | Trainer | Dietitian | Practitioner | Administrator |
|---|---|---|---|---|---|
| Own data and permissions | View/manage | — | — | — | Restricted support |
| Prescribe and adjust workouts | View/perform | Create for assigned clients | — | — | — |
| Physical assessment | View | Create for assigned clients | View if authorized | View if authorized | — |
| Meal plan and food diary | View/log | Summary if authorized | Prescribe/follow assigned clients | — | — |
| Therapy record and session | View | — | — | Create for assigned clients | — |
| Heart rate through HealthKit | Connect/authorize/revoke | View authorized data | View authorized data | View authorized data | Operate without content by default |

## 7. Nonfunctional and Compliance Requirements

| ID | Verifiable requirement |
|---|---|
| RNF-01 | Health data is sensitive. For each purpose, define legal basis, controller/processor roles, retention, sharing, and disposal. Record and allow withdrawal of consent when consent is the chosen basis. Apply least-privilege access. |
| RNF-02 | Encrypt data in transit and at rest; isolate organizations; require MFA for professionals and administrators; allow session revocation; test backups; log access to sensitive data without writing clinical content to logs. |
| RNF-03 | Audit reads, changes, exports, access grants and revocations, and prescription publications, including time and actor identity. |
| RNF-04 | Export a user's own data in readable and structured formats. Provide access, correction, and deletion request flows subject to retention obligations. Show a clear privacy policy before connecting Apple Health. |
| RNF-05 | Initial API availability target: 99.5% monthly, with error monitoring, synchronization queue, and tested backup restore. Set final RPO/RTO targets before launch. |
| RNF-06 | Target home and workout-list load times of at most 3 seconds at p95 on a suitable mobile connection. Recording a set should respond locally within 300 ms and synchronize reliably. |
| RNF-07 | Mobile app in Brazilian Portuguese on iOS and Android, supporting larger fonts and screen readers; meet WCAG 2.2 AA for contrast, labels, touch targets, and keyboard navigation where applicable. |
| RNF-08 | Provide instructional text or captions for videos, compression and controlled caching, and rights to use all third-party photos and media. |
| RNF-09 | Offline synchronization tolerates retries and conflicts. Each write has an idempotent identifier, original timestamp, and delivery state. |
| RNF-10 | Before claiming disease detection, clinical risk assessment, emergency monitoring, or automated treatment recommendations, assess whether the intended function is regulated as software as a medical device. |

## 8. Integrations and Dependencies

| Integration | Proposed approach | Dependency or limit |
|---|---|---|
| Apple Health | HealthKit on iPhone | Granular permission to read heart rate and separate permission to save completed workouts. Available samples depend on what each source stores in Apple Health. |
| Payments | Payment service provider with Pix and cards | Provider selection, fees, and reconciliation require commercial discovery. |
| Messaging | Push/email and user-initiated WhatsApp | Keep sensitive messages in the app; verify provider costs and rules. |

Integration documentation: [HealthKit authorization](https://developer.apple.com/documentation/HealthKit/authorizing-access-to-health-data), [sample queries](https://developer.apple.com/documentation/healthkit/hkanchoredobjectquery), [saving a workout](https://developer.apple.com/documentation/healthkit/hkworkoutbuilder).

## 9. Care Safety and Professional Responsibilities

- **Nutrition:** Limit dietary prescriptions to qualified professionals under [Brazilian Law 8,234/1991](https://www.planalto.gov.br/ccivil_03/leis/1989_1994/l8234.htm). Trainers see only what the client and dietitian authorize for workout coordination.
- **Acupuncture:** Verify qualifications and scope under [Brazilian Law 15,345/2026](https://www.planalto.gov.br/ccivil_03/_ato2023-2026/2026/lei/l15345.htm); review the relevant professional council's rules and protocols before enabling each practitioner.
- **Privacy:** Brazil's [ANPD](https://www.gov.br/anpd/pt-br/acesso-a-informacao/perguntas-frequentes) classifies health information as sensitive personal data. Define a legal basis and purpose for each processing activity instead of assuming a single consent covers every use.
- **Software purpose:** [Anvisa](https://www.gov.br/anvisa/pt-br/centraisdeconteudo/publicacoes/produtos-para-a-saude/manuais/software-como-dispositivo-medico-perguntas-e-respostas) applies specific rules to medical software. The initial scope is exercise and wellness tracking; any diagnostic or automated clinical decision feature requires its own assessment before development and promotion.

## 10. Delivery Phases

| Phase | Scope and completion condition |
|---|---|
| **MVP — P0** | Tenant creation and isolation; accounts, memberships, and client-professional relationships; client dashboard; exercise library and versioned prescriptions; correct execution and attendance; basic assessments; feedback and progress; nutrition plan and diary; therapy scheduling, records, and consent; HealthKit reading, heart-rate upload to the API, and optional workout saving to Apple Health; granular permissions, audit, and export. Complete when each role has a demonstrable end-to-end journey and cross-tenant isolation tests pass. |
| **Phase 2 — P1** | Messaging, broader scheduling, extra workouts, periodization, goals, offline support, billing, reports, and workout zones. Complete when financial reconciliation and resilient synchronization have been tested. |
| **Phase 3 — P2** | Advanced HealthKit data analysis only after data-quality validation and regulatory review. |

## 11. Priority Acceptance Test Cases

1. A client linked to two professionals receives two workout plans without data mixing; each professional sees only assigned clients and authorized metrics.
2. A trainer publishes version B while the client performs version A; the execution remains linked to A and the next opening presents B.
3. Opening a video and leaving does not increase attendance; completing a session counts on the correct local date; rest days are not marked missed.
4. Repeated heart-rate synchronization imports a sample once; a source-deleted sample follows the synchronization policy; the chart shows gaps and the last sync time.
5. A client denies or revokes heart-rate access: workouts remain usable and no new data appears for professionals.
6. A dietitian revises a plan: the client sees the new version and history; an unauthorized trainer cannot see the food diary.
7. An unverified professional cannot publish dietary prescriptions or document acupuncture procedures.
8. An acupuncture session requires consent and mandatory fields before completion.
9. A user requests an export: the package contains their records and provenance, no third-party data, and the audit log records request and delivery.
10. On iPhone, denying heart-rate read access keeps workouts functional; granting access permits samples from different Apple Health sources without source filtering.
11. Heart-rate samples arrive during a workout: the screen and API associate each with the correct session; retries do not duplicate points; mean, maximum, and progression reflect accepted points.
12. A client finishes a workout and denies Apple Health write access: API completion succeeds and no Health workout is created. With write access, exactly one workout is saved with the correct interval.
13. New samples appear in Apple Health after a workout or connectivity fails during upload: pending data is reconciled without duplicating the session or reverting its completed state.
14. A professional with memberships in two tenants switches context: only the selected tenant's clients, files, notifications, and records appear; known IDs and file URLs from the other tenant fail authorization.
15. A client linked to two organizations sees both relationships in their own view; professionals in either organization cannot see the other's records without a separate permitted sharing flow.

## 12. Business Decisions Before Implementation

1. Initial market: independent professionals, clinics or teams, or both; all use the same tenant isolation model, while the initial market determines onboarding and billing flows.
2. Commercial model: charge professionals, clients, per module, or per organization; decide who invoices the client.
3. Nutrition MVP depth: a simple diary and prescribed plan (proposed) versus full nutrient calculation with a food database.
4. Launch therapy modalities beyond acupuncture and the qualification-verification process.
5. Care-record and document retention, contracts between organizations and professionals, and legal review of processing bases.
6. Expected heart-rate update frequency during workouts and interface behavior when samples arrive late.

