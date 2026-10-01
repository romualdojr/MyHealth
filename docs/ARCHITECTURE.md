# MyHealth API — Architecture

## Layers (Clean Architecture)

```
MyHealth.ApiService      Minimal-API endpoints, auth, ProblemDetails, OpenAPI      ─┐
MyHealth.Infrastructure  EF Core/PostgreSQL, Identity, repositories, audit, UoW     │ depend
MyHealth.Application     CQRS commands/queries + handlers, validation, access rules │ inwards
MyHealth.Domain          Aggregates, value objects, domain events, Result/Error     ◄┘
MyHealth.AppHost         Aspire orchestration (PostgreSQL + pgAdmin + API)
```

The dependency rule is enforced by `tests/MyHealth.ArchitectureTests`.

## Bounded contexts (Phase 1)

| Context | Aggregates | Notes |
|---|---|---|
| Users | `UserProfile` | Id = Identity user id. Holds the IANA time zone used for attendance. |
| Professionals | `ProfessionalProfile` | One per user per discipline; admin verification; registration change resets verification. |
| Care relationships | `CareRelationship` (+ `DataSharingGrant`) | pending → active → ended. Per-category grants with full history. |
| Training | `Exercise`, `TrainingPlan`, `TrainingPlanVersion`, `WorkoutSession` | Draft is editable; publishing creates an immutable version (jsonb). Sessions reference the version. |
| Health data | `HealthConnection`, `WorkoutHeartRate` | Apple Health connect/pause/revoke; heart-rate ingestion with dedup, finalisation and reconciliation. |
| Notifications | `Notification` | Created by domain-event handlers in the same transaction. |

## Key decisions

- **CQRS without a mediator library.** Endpoints inject `ICommandHandler<,>` / `IQueryHandler<,>` directly; Scrutor registers handlers and wraps them in validation and logging decorators. (MediatR is now commercially licensed and adds little here.)
- **Write side:** repositories per aggregate + `IUnitOfWork`. **Read side:** `IReadDbContext` (no-tracking `IQueryable`) projected straight to response records.
- **Result pattern** for expected failures (`Result`, `Error`, `ValidationError`) → RFC 9457 problem details. Exceptions are only for bugs; concurrency/unique violations map to 409 so phones retry.
- **Domain events** are dispatched by the unit of work *before* commit, so side effects (notifications) are atomic with the change. An outbox for external delivery (push/e-mail) is Phase 2.
- **Idempotency by design:** client-generated ids for workout sessions and set logs; heart-rate `PUT` is a snapshot upsert with a DB unique index on `(workout, HealthKit sample UUID)`; optimistic concurrency via PostgreSQL `xmin`.
- **Data isolation:** every professional action goes through `ICareAccess` (own profile for the discipline → verified → active relationship → active grant). Resources outside the caller's scope return 404, not 403, to avoid leaking existence.
- **Privacy:** request payloads are never logged; audit entries hold metadata only (actor, action, resource, subject). Notifications contain pointers, not clinical content.
- **Identity:** ASP.NET Core Identity API endpoints (`/api/v1/auth/*`) with bearer tokens, lockout, e-mail confirmation and 2FA endpoints. MFA *enforcement* for professionals/admins is Phase 2. The domain never sees credentials.

## Heart-rate contract (server side of `docs/initial_requirements/ios`)

`PUT /api/v1/workout-sessions/{id}/heart-rate` accepts the iOS `WorkoutHeartRateReport` unchanged.

| Report | Behaviour |
|---|---|
| `endedAt = null` (in progress) | Additive. New samples accepted, identical ones counted as duplicates, changed ones recorded as corrections. |
| `endedAt` set, first time | **Final.** Samples missing from the snapshot are marked *removed by source* (kept for history). Summary recalculated. |
| `endedAt` set, again | **Reconciliation** (HK-08): late samples / deletions applied, revision and `lastRevisedAt` bumped. |
| `endedAt = null` after final | **Ignored as stale** (HK-07): cannot revert the completed state. |

Samples must be 20–300 bpm, inside the server's session interval, and outside periods when the client had paused or revoked import. A HealthKit sample is stored once per client (unique index on `client_id, external_id`). Phone-computed mean/max are ignored; the API stores an arithmetic and a time-weighted mean, each with its method name, plus a count of self-reported (`wasUserEntered`) samples. Uploads are refused while Apple Health is paused or revoked, and reconciliation is accepted for 30 days after the workout ends (`GET /me/health-connections/apple-health/reconciliation` lists what to re-query). Professionals read heart rate only through an active relationship with an active `HeartRate` grant while the client has not revoked Apple Health, and every read is audited.

Saving workouts *to* Apple Health is a separate opt-in (`PUT /me/health-connections/apple-health/workout-saving`); the phone reports the saved HKWorkout with `PUT /workout-sessions/{id}/apple-health-workout`, and only one per session is accepted. The full iPhone contract is in `docs/initial_requirements/ios/README.md`.

## Running

```bash
dotnet run --project src/MyHealth.AppHost        # PostgreSQL + API (+ the template web front end)
```

API reference (Development): `/scalar/v1`. Migrations are applied on startup in Development.

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/MyHealth.Infrastructure -s src/MyHealth.ApiService -o Persistence/Migrations
dotnet test                                      # unit + architecture + integration (integration needs Docker)
```
