# MyHealth

Backend API for a training-first health app for clients, personal trainers, dietitians and therapy practitioners (.NET 10, Clean Architecture, DDD, CQRS, PostgreSQL, Aspire).

- Requirements review and delivery phases: [docs/REQUIREMENTS_REVIEW_AND_PHASES.md](docs/REQUIREMENTS_REVIEW_AND_PHASES.md)
- Architecture and key decisions: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- To-do and open decisions: [docs/TODO.md](docs/TODO.md)
- Trainer workspace phases (web plan builder, 3D videos, WhatsApp, AI coach): [docs/TRAINER_WORKSPACE_PHASES.md](docs/TRAINER_WORKSPACE_PHASES.md)
- Source requirements: [docs/initial_requirements](docs/initial_requirements)

## Quick start

```bash
dotnet run --project src/MyHealth.AppHost   # needs Docker for PostgreSQL
dotnet test                                 # unit, architecture and integration tests
npm ci --prefix src/MyHealth.Web.Common      # once, to rebuild the Tailwind CSS of the web apps when markup changes
```

In Development the API applies migrations on startup and serves an API reference at `/scalar/v1`.

### Development accounts

In Development the API also seeds sample data once (`Seed:Development` in `src/MyHealth.ApiService/appsettings.Development.json`,
where the shared password is). All accounts use that password; two-factor is off in Development.

| Account | Use it in | What it has |
|---|---|---|
| `operador@myhealth.dev` | Back office | Platform operator |
| `treinador@myhealth.dev` | Web dashboard | Verified personal trainer, administrator of "Estúdio Movimento" (trial), 2 clients, plans, workouts, a custom exercise |
| `nutri@myhealth.dev` | Web dashboard | Verified dietitian, member of the same organization |
| `ana@myhealth.dev` | Phone app / API | Client: published plan, 5 workouts, wellbeing, AI analysis allowed (the coach proposes a progression) |
| `bruno@myhealth.dev` | Phone app / API | Client: one workout with a discomfort report awaiting the trainer's reply |
| `carla@myhealth.dev` | Phone app / API | Invitation from the trainer still pending |

To start over, delete the `myhealth-postgres-data` Docker volume (or set `Seed:Development:Enabled` to `false` to skip it).
