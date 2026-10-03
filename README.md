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
