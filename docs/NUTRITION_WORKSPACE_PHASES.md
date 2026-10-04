# Nutrition workspace (web) — phases

The web module a dietitian uses to assess clients, prescribe meal plans and follow the food diary. Same goal as the
[trainer workspace](TRAINER_WORKSPACE_PHASES.md): **clear and easy** — from "new client" to "meal plan on the client's
phone" in a few minutes.

## Requirements in scope

| Id | Priority | Requirement (from [APP_REQUIREMENTS.md](initial_requirements/APP_REQUIREMENTS.md) §4.6) | API status |
|---|---|---|---|
| RF-28 | P0 | Nutrition assessment: goals, restrictions and allergies, preferences, meal times, measurements, follow-up. Not visible to the trainer. | ✅ record, list, read, correct with a reason |
| RF-29 | P0 | Versioned meal plan: dates, meals and times, foods and portions, substitutions, notes, author. Publishing notifies the client and keeps previous versions. Only a qualified dietitian prescribes. | ✅ draft → publish with change description → immutable versions; end |
| RF-30 | P0 | Client food diary: meal, time, text or photo, water, adherence; no punitive language. The dietitian comments and revises the plan. | ✅ diary per client and date range, comments; ❌ photos (need RF-27) |
| RF-31 | P1 | Individual nutrition goals and period follow-up; nutrient calculations show source, units and approximation; activity never generates intake prescriptions automatically. | ❌ |
| RF-32 | P1 | Allergy and restriction warnings in the editor and in the client's plan; supplements need dietitian review. | ❌ |

Rules that apply across all of them:

- **Professional boundaries** (§9, Law 8,234/1991): only a verified dietitian, in a membership with the Nutrition
  discipline, creates, edits or publishes; suspension stops publishing at once (2.7 N9). Trainers never prescribe diets.
- **Sharing** (§6, RF-43): the trainer sees only the `NutritionSummary` (active plans and 7-day adherence counts) with the
  client's grant — never foods, assessments or diary text (2.7 N7). Diary entries are addressed to one dietitian
  relationship or stay private (2.7 N6).
- **Multi-tenancy** (MT-04): every record carries tenant, client and author; tenant comes from the membership, never from input.
- **Module billing** (2.9 B1): the workspace is available only to tenants subscribed to the Nutrition module.

## Design rules

The same as the trainer workspace: one task per screen, one primary button; nothing reaches the client until *Publicar*
(with a change description); Portuguese UI, WCAG 2.2 AA; the API is the authority and every page has contract tests.

## Phase 1 — Prescribe and follow (P0 on the web)

| Screen | What the dietitian does | API |
|---|---|---|
| Clientes (`/dietitian`) | Sees active clients, plans, diary entries without a reply and next follow-up dates; invites a client. | ⚠️ needs a dietitian dashboard query (the trainer has `/trainer/dashboard`) |
| Cliente (`/dietitian/clients/{id}`) | Tabs: assessments, meal plans, food diary. | ✅ |
| Avaliação | Records an assessment (goals, restrictions, allergies, preferences, meal times, measurements, follow-up); corrects one with a reason. | ✅ |
| Plano alimentar (`/dietitian/plans/{id}`) | Edits details (name, goal, dates, notes); adds meals with times; foods with quantity, unit, household measure, substitutions and notes; saves the draft; publishes with a change description; sees version history; ends the plan. | ✅ |
| Diário | Reads entries per day (meal, time, text, water, adherence) against the plan; comments; jumps to the plan to revise it. | ✅ |

Acceptance: tests 6 and 7 (nutrition part) passing through the web, plus e2e tests like the trainer workspace.

## Phase 2 — Comfort features

- Copy a plan to another client, and plan templates without a client.
- Duplicate a meal or a day; reorder meals and foods.
- Adherence view: planned vs logged per meal over the week.
- Notify the dietitian of new diary entries (outbox, same as trainer feedback).

## Phase 3 — P1 nutrition (RF-31, RF-32)

| Concern | Open question |
|---|---|
| Goals and follow-up periods (RF-31) | Structured goals (weight, measurements, habits) or free text? Reuse the goals of RF-26. |
| Nutrient calculations (RF-31) | Which food table (e.g. TACO, TBCA)? Licence? Show source, units and approximation on every number. Revisits 2.7 N8 (MVP has no nutrient database). |
| Allergy warnings (RF-32) | Needs allergies as coded values instead of the free text the assessment stores today, and foods linked to the table. |
| Supplements (RF-32) | A separate item type that requires the dietitian's review and a note on relevant clinical conditions. |

## Phase 4 — Photos and integrations

- Meal photos in the diary and assessment photos — wait for file storage (RF-27, MT-07/MT-09).
- AI help for the dietitian (like the trainer's AI coach): only after the AI provider's DPA and legal review; needs its own sharing category.

## Decisions needed

- Must the dietitian also authorize the trainer's nutrition summary, or is the client's grant enough? (2.7 N7)
- Food table and its licence for RF-31.
- Retention period for assessments, plans and diary entries (§12.5).
- Nutrition module price (2.9 B1–B3).

## Order and dependencies

```
Phase 1 (web prescribe/follow; dashboard query in the API) ──► Phase 2 (templates, adherence, notifications)
Food table decision ──► Phase 3 (RF-31/32)
RF-27 file storage ──► Phase 4 (photos)
```
