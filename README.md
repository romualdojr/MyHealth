# MyHealth

Backend API for a training-first health app for clients, personal trainers, dietitians and therapy practitioners (.NET 10, Clean Architecture, DDD, CQRS, PostgreSQL, Aspire).

- Requirements review and delivery phases: [docs/REQUIREMENTS_REVIEW_AND_PHASES.md](docs/REQUIREMENTS_REVIEW_AND_PHASES.md)
- Architecture and key decisions: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Source requirements: [docs/initial_requirements](docs/initial_requirements)

## Quick start

```bash
dotnet run --project src/MyHealth.AppHost   # needs Docker for PostgreSQL
dotnet test                                 # unit, architecture and integration tests
```

In Development the API applies migrations on startup and serves an API reference at `/scalar/v1`.
