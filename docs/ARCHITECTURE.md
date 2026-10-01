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
| Tenants | `Tenant`, `TenantMembership` | The service organization (clinic, team or solo practice) that owns care records. Memberships hold the disciplines a person practises there and the tenant-admin flag. |
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
- **Data isolation:** every professional action goes through `ICareAccess` (active membership in the selected tenant including the discipline → own profile for the discipline → verified → active relationship *in that tenant* → active grant). Resources outside the caller's scope return 404, not 403, to avoid leaking existence. See *Multi-tenancy* below.
- **Privacy:** request payloads are never logged; audit entries hold metadata only (actor, action, resource, subject). Notifications contain pointers, not clinical content.
- **Identity:** ASP.NET Core Identity API endpoints (`/api/v1/auth/*`) with bearer tokens, lockout, e-mail confirmation and 2FA endpoints. MFA *enforcement* for professionals/admins is Phase 2. The domain never sees credentials.

## Multi-tenancy (§4.1.1, MT-01 → MT-10)

Shared database, shared schema, a `tenant_id` column on every tenant-owned row. Accounts are global; a person works
inside a tenant through a `TenantMembership`.

| Concern | Decision |
|---|---|
| What is global | `UserProfile`, `ProfessionalProfile` (a council registration belongs to the person, so it is verified once), `HealthConnection` (the client's own Apple Health consent). |
| What carries `tenant_id` | `CareRelationship`, `TrainingPlan`, `TrainingPlanVersion`, `WorkoutSession`, `WorkoutHeartRate` (inherited from the session, MT-06), custom `Exercise` (`null` = curated library), `Notification` (`null` for account notices), `AuditEntry` (`null` for platform actions). Foreign keys to `tenants` everywhere except audit, which must accept denied attempts naming unknown tenants. |
| Selecting a tenant | The client sends `X-Tenant-Id`. `ITenantContext` honours it only for an **active membership** of the caller (MT-04); otherwise `Tenant.NotFound` (404) and a `deny` audit entry. Professional endpoints without the header return `Tenant.Required` (400). It is resolved once per request and nothing is cached server-side between requests, so switching tenants is just sending another header (the apps must drop their own tenant-scoped caches, MT-07). |
| Client view | `/me/*` and the client's workout/heart-rate endpoints need no header and span every tenant (MT-10). Records are labelled with `tenantId`/`organizationName` and the responsible professional. Client writes derive the tenant from the resource (plan → session → heart rate), never from input. |
| Professional view | Every query and command filters by the active tenant **and** the caller's profile. A known id from another tenant gives 404 and is audited as `deny` (MT-09); `ICareAccess` and `TrainerPlanLoader` are the choke points. Notifications with audience `Professional` are only listed in their tenant's context. |
| Roles | *Tenant administrator* (membership flag): invites/revokes members and reads the tenant's audit trail — no clinical access unless they also practise and have a relationship (MT-05). *Platform administrator* (Identity role): verifies credentials, lists tenants, reads audit across tenants — no access inside any tenant. |
| Leaving a tenant | Revoking a membership raises `TenantMembershipRevoked`; in the same transaction the person's pending/active relationships in that tenant end (grants revoked). Records stay with the tenant (MT-08). The last administrator cannot be removed. |
| Existing data | Migration `MultiTenancy` gives every pre-existing professional a solo-practice tenant (id = user id) with an administrator membership, and attaches their records to it. |

Not yet implemented (needed as the related features arrive): client reassignment/transfer between professionals of a
tenant (RF-05), tenant suspension for billing, per-tenant settings, and the same tenant checks for files, exports and
background jobs (MT-07/MT-09). PostgreSQL row-level security would add defense in depth behind the application checks.

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
