# Configuration Guide

This project separates configuration into two categories:

1. `appsettings.json` and `appsettings.{Environment}.json`:
   Non-secret application configuration and schema mapping.
2. `.env` files / environment variables:
   Credentials and runtime overrides only.

## HubSpot Configuration Rules

### Keep in `appsettings.json` (non-secret schema mapping)

These keys define how HubSpot data is interpreted and mapped in code:

- `HubSpot:FulfilledProperty`
- `HubSpot:FulfilledValue`
- `HubSpot:DealNameProperty`
- `HubSpot:OwnerEmailProperty`
- `HubSpot:OwnerIdProperty`
- `HubSpot:FulfilledDateProperty`
- `HubSpot:LastModifiedProperty`
- `HubSpot:AmountProperty`
- `HubSpot:CurrencyCodeProperty`
- `HubSpot:UsernameEmailDomain`

Why: these are not secrets; they describe field mapping and business rules.

### Keep in `.env` / environment variables (secrets and runtime overrides)

- `HubSpot__AccessToken` (secret)
- `HubSpot__Enabled` (runtime toggle)
- `HubSpot__BaseUrl` (runtime endpoint override)
- `HubSpot__SyncCron` (runtime scheduling override)

You may also keep SMTP credentials and DB credentials in `.env`.

## Startup Validation

On app startup, HubSpot options are validated:

- Required schema mapping keys must be non-empty.
- If `HubSpot:Enabled=true`, then `HubSpot:AccessToken` must be present.

If validation fails, startup fails fast with a clear error.

## Practical Usage

1. Define schema mapping in `appsettings.json`.
2. Put credentials in `.env` (or container/host environment variables).
   In Development, `.env.dev` is also loaded automatically if present.
3. Keep `.env` files out of Git (except templates like `.env.example`).
