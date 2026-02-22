# Deploy To DigitalOcean With doctl

This project can be deployed to DigitalOcean App Platform using:

- `doctl` for infrastructure/deploy operations
- DO Container Registry (DOCR) for the app image

## Prerequisites

- `doctl` installed
- `docker` installed and running
- `jq` installed
- A DigitalOcean API token with write access
- A reachable SQL Server connection string for production

## One-Time Auth

```bash
export DO_ACCESS_TOKEN="<your-digitalocean-token>"
```

## Required Environment Variables

```bash
export DO_REGISTRY_NAME="<your-docr-registry-name>"
export DB_CONNECTION_STRING="Server=<host>,1433;Database=<db>;User Id=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True"
```

## Optional Environment Variables

```bash
export DO_APP_NAME="netapp"
export DO_REGION="fra"
export DO_IMAGE_REPO="netapp-app"
export DO_IMAGE_TAG="$(git rev-parse --short HEAD)"
export DO_INSTANCE_SIZE="apps-s-1vcpu-0.5gb"
export DO_INSTANCE_COUNT="1"
export DATABASE_AUTO_MIGRATE_ON_STARTUP="true"

export Security__DefaultAdminUsername="devsuperadmin"
export Security__DefaultAdminPassword="<set-strong-password>"
export Security__DefaultAdminEmail="devsuperadmin@localhost"

export HubSpot__Enabled="true"
export HubSpot__AccessToken="<hubspot-token>"
```

## Deploy

```bash
./scripts/deploy_digitalocean_app.sh
```

The script will:

1. Authenticate `doctl` (if `DO_ACCESS_TOKEN` is set)
2. Ensure the DOCR registry exists
3. Build and push the Docker image
4. Create or update the App Platform app

## Notes

- `DB_CONNECTION_STRING` is injected as `ConnectionStrings__DefaultConnection`.
- The app requires SQL Server compatibility (`Microsoft.Data.SqlClient` / EF SQL Server provider).
- If you run migrations on startup, ensure the DB user can apply schema changes.
