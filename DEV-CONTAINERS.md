# Containerized Development Setup

This setup runs the app and SQL Server locally in Docker and overrides connection/email settings so development does not touch production systems.

## What It Starts

- `app`: ASP.NET Core app via `dotnet watch run` on `http://localhost:8080`
- `db`: Local SQL Server 2022 on host port `14333`

## Start

```bash
docker compose -f docker-compose.dev.yml up --build
```

## Stop

```bash
docker compose -f docker-compose.dev.yml down
```

## Reset Local Database

```bash
docker compose -f docker-compose.dev.yml down -v
```

This removes the SQL volume `netapp_sql_data` and recreates an empty local DB on next start.

## Safety Guarantees in This Setup

- `ConnectionStrings__DefaultConnection` is forced to `Server=db,1433;...` (container SQL Server).
- SMTP credentials are overridden with empty values, so notification emails are skipped.
- `Database__AutoMigrateOnStartup=false` skips auto-migrations during container startup.
- A default dev admin is seeded automatically:
  - Username: `devsuperadmin`
  - Password: `ChangeMe123`
  - Email: `devsuperadmin@localhost`
