# Trainer workspace (web) — phases

The web module a personal trainer uses to build and follow up training. Goal: **clear and easy** — a trainer should go
from "new client" to "plan published on the client's phone" in a few minutes, without reading a manual.

Builds on what exists: the training API (exercise library, plans with draft → immutable versions, workout sessions,
feedback replies, trainer dashboard), exercise media (loops per angle, full video), notifications outbox and the
assistant model abstraction (`IAssistantModel`, fake + Claude). See [ARCHITECTURE.md](ARCHITECTURE.md).

## Design rules for every phase

- **One task per screen, one primary button.** Client → plans → plan editor. The editor saves the draft on demand and
  says plainly what the client sees (the last published version) versus what is being edited (the draft).
- **Nothing reaches the client by accident.** Only *Publicar* changes what the client performs; it always asks for a short
  change description (kept in the version history).
- **Portuguese UI, accessible** (labels on every input, keyboard-only use, WCAG 2.2 AA target).
- **The API stays the authority.** The web app is an HTTP client; every page is covered by the contract tests.
- **Integrations sit behind an interface with a fake implementation**, so development, tests and demos never need the
  real provider, and the provider can be swapped (same pattern as payments and the assistant).

## Phase 1 — Plan builder on the web ✅ (this branch)

| Screen | What the trainer does |
|---|---|
| Clientes (`/trainer`) | Sees active clients with adherence and pending items; invites a client by e-mail; opens a client. |
| Cliente (`/trainer/clients/{id}`) | Plans of that client (status, version, unpublished changes, review flag); creates a plan; copies a plan to another client; recent workouts with feedback, discomfort and body checks; replies to feedback. |
| Plano (`/trainer/plans/{id}`) | Edits details (name, goal, level, dates, sessions per week); adds strength or aerobic sessions; picks exercises from the library (search by name, muscle, equipment); sets, repetitions or duration, load, rest, instructions; reorders; saves the draft; publishes with a change description; sees the version history; acknowledges a review flag; ends the plan. |
| Biblioteca (`/trainer/exercises`) | Searches the library; creates and edits the organization's own exercises. |

Out of scope here (later): drag and drop, plan templates without a client, periodisation (RF-15), heart-rate views.

## Phase 2 — Media upload and storage (prerequisite for 3D) — minimal version built

RF-27, already in the backlog. Signed uploads to object storage, tenant checks (MT-07/MT-09), transcoding to adaptive
streaming, thumbnails, the media host (`Media:AllowedHosts`). Trainers upload a phone video of an exercise from the
library screen. Without this, neither the trainer's recordings nor the 3D renders have anywhere to live.

## Phase 3 — 3D exercise videos (DeepMotion) ✅ (DeepMotion API to validate in its sandbox)

The trainer films themselves doing an exercise once; MyHealth turns it into a 3D avatar animation and renders it as
loops from several camera angles (side / front / back), so every exercise gets consistent, licence-free demo videos.

| Concern | Decision |
|---|---|
| Provider | DeepMotion Animate 3D (video → 3D motion; renders MP4 and exports FBX/GLB). Behind `IMotionCaptureProvider` with a `FakeMotionCaptureProvider`. **(confirm "deepmovie" = DeepMotion, plan and credits per minute)** |
| Flow | Upload source video (Phase 2) → `MotionCaptureJob` aggregate (tenant, exercise, source media, status `Queued → Processing → Succeeded/Failed`, provider job id, credits used) → background poller / webhook → rendered angles copied to our media host → **proposed** as `Loop` media on the exercise; the trainer previews and accepts. |
| Privacy | The source video shows the trainer (biometric-like data): explicit consent screen before upload, source deleted after processing (configurable), never client videos. DPA with the provider. |
| Cost control | Credits per tenant per month (billing module flag "3D videos"), job size limits (duration, resolution), operator view of usage. |
| Library | Operators can run the same flow for the curated library (answers TODO "who films the library videos"). |

## Phase 4 — WhatsApp ✅ generic interface (provider to be chosen)

Requirement RF-08 and §8: sensitive content stays in the app; WhatsApp only carries **pointers**.

| Concern | Decision |
|---|---|
| Click-to-chat (no provider) | A "WhatsApp" button on the client screen opens `wa.me/<phone>` with a neutral text typed by the trainer — user-initiated, nothing automatic. Needs the client's phone on the profile with the client's consent. |
| Automatic messages | WhatsApp Business Platform (Meta Cloud API, or a BSP such as Twilio/Zenvia) as a **new delivery channel of the notification outbox** (`IWhatsAppSender`, fake by default). Only pre-approved templates with generic text + deep link: "Seu novo treino está disponível", workout reminders, "Seu treinador respondeu". **(decide provider, template approval)** |
| Consent | Client opt-in per channel (LGPD), opt-out by replying "SAIR" (inbound webhook), stored with history like sharing grants. |
| Inbound | Phase 4 only handles opt-out and "talk to the app" replies; no clinical conversation over WhatsApp. |

## Phase 5 — AI coach for the trainer (LLM) ✅

Helps the trainer adapt each client's plan; **suggests, never decides**.

| Concern | Decision |
|---|---|
| What it reads | For one client, only what the trainer may already see: the current plan, set logs vs targets (loads, reps, RPE), adherence, feedback and discomfort reports, body checks; wellbeing and health history only with the client's grants. Pseudonymised (no name, e-mail). |
| What it returns | Structured suggestions through tools, e.g. `progress_load`, `deload`, `swap_exercise` (only library ids from the context), `adjust_volume`, `add_note` — each with a reason citing the data. Shown as cards in the plan editor; *Aplicar* changes the **draft** only, the trainer still publishes. |
| Guard rails | No diagnosis or medical advice; discomfort reports produce "talk to the client / refer" suggestions, never load increases; Portuguese; rate and cost limits per tenant; each request audited (no content). |
| Model | `ITrainingCoachModel` with a rule-based fake (progressive overload / deload heuristics, also useful offline) and Claude via the Messages API with tool use, as the client assistant does. |
| Consent / legal | Data processing agreement with the AI provider; client-facing disclosure and a sharing category for AI analysis. **(decide with legal)** |
| Later | Generate a first plan draft from goal/level/equipment; weekly summary per client. |

## What was built

See [ARCHITECTURE.md](ARCHITECTURE.md) § "Trainer integrations" for the decisions and [TODO.md](TODO.md) § "Trainer workspace" for
what is left (provider choices, legal review, production storage, phone apps).

- Phase 2: `IMediaStorage` with a local-disk implementation and `/media/` serving for development; uploads pass through the API.
- Phase 3: the trainer films the movement, the result is a glTF 3D model (`MediaKind.Model3D`) plus rendered loops; the source video is
  deleted after processing; the exercise library has an upload panel with a 3D preview.
- Phase 4: `IWhatsAppProvider` with a fake; opt-in with a code, opt-out (app or SAIR), notification channel, click-to-chat.
- Phase 5: the coach runs on rules by default and on Claude when configured; the plan editor has an "Assistente de treino (IA)" panel.

## Order and dependencies

```
Phase 1 (web plan builder) ──► Phase 5 (AI coach: cards inside the editor)
Phase 2 (media upload/CDN) ──► Phase 3 (3D videos)
Phase 4 (WhatsApp) — independent; click-to-chat can ship any time, automatic messages need a provider
```
