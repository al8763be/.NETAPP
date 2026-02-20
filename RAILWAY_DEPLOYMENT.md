# Railway Deployment (Docker)

This project now includes a production `Dockerfile` at the repository root for Railway.

## 1) Build Locally (Optional)

```bash
docker build -t netapp-demo:latest .
```

## 2) Required Railway Environment Variables

Set these in Railway service variables:

- `ConnectionStrings__DefaultConnection`
- `Security__DefaultAdminUsername`
- `Security__DefaultAdminPassword`
- `Security__DefaultAdminEmail`

Recommended for first deploy:

- `Database__AutoMigrateOnStartup=true`

Optional:

- `HubSpot__Enabled=false` (if you do not want HubSpot sync in demo)

Notes:

- Railway provides `PORT` automatically. The container command binds to it.
- `ASPNETCORE_ENVIRONMENT` defaults to `Production` in the Docker image.

## 3) Deploy to Railway

1. Create a new Railway service from this repo.
2. Ensure Railway uses the root `Dockerfile`.
3. Add the variables above.
4. Deploy.

## 4) Verify

- Open the generated Railway URL.
- Log in with the configured admin credentials.
- If demo data is needed, use your local preview script (kept outside version control):

```bash
scripts/seed_superadmin_preview_deals.sh
```
