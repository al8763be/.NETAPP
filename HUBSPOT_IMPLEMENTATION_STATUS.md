# HubSpot Integration Status (Point of Truth)

Last updated: 2026-02-22

## Scope and Priority

This status is now aligned to the 3 original instructions only:

1. Create mapping/data table for HubSpot owner information (linked to local user identity).
2. Add tests to validate that owner/deal mapping information is received and stored correctly.
3. Expand `Min profil` to show stored HubSpot owner info + current month fulfilled sales + estimated total.

Admin UI for manual mapping is explicitly deferred until after these 3 parts are completed.

## Current Diagnosis

- `POST /Social/SyncHubSpotDeals` returning `302` is expected (PRG redirect), not a transport failure.
- Sync has been running, but many deals were skipped due to unresolved owner-to-local-user mapping.
- Deals frequently contain `hubspot_owner_id`; deal-level owner email may be missing.

## Implemented So Far

### Part 1: Owner mapping/data table
Status: Implemented

Delivered:
- New model: `Models/HubSpotOwnerMapping.cs`
- DbSet and configuration in `Data/STLForumContext.cs`
  - unique `HubSpotOwnerId`
  - optional FK to `AspNetUsers` (`OwnerUserId`)
  - username snapshot (`OwnerUsername`)
- Migration created/applied:
  - `Migrations/20260219122732_AddHubSpotOwnerMappings.cs`
- Sync service now:
  - resolves owner details by `hubspot_owner_id`
  - upserts owner metadata into `HubSpotOwnerMappings`
  - attempts local-user resolution via persisted mapping first, then email fallback

Related files:
- `Services/HubSpot/IHubSpotClient.cs`
- `Services/HubSpot/HubSpotClient.cs`
- `Services/HubSpot/HubSpotSyncService.cs`
- `Services/HubSpot/HubSpotOptions.cs`
- `appsettings.json`

### Supporting reliability fixes
Status: Implemented

- Added HubSpot core tables migration baseline:
  - `Migrations/20260219105408_AddHubSpotTables.cs`
- Development startup now loads `.env.dev`:
  - `Program.cs`
- Explicit decimal precision for imported deal amount:
  - `Data/STLForumContext.cs`

## Part 2: Tests
Status: Implemented and validated

Delivered:
- New test project:
  - `WebApplication2.Tests/WebApplication2.Tests.csproj`
- Test helpers:
  - `WebApplication2.Tests/Helpers/TestIdentityEnvironment.cs`
  - `WebApplication2.Tests/Helpers/FakeHubSpotClient.cs`
- HubSpot sync coverage:
  - `WebApplication2.Tests/Services/HubSpot/HubSpotSyncServiceTests.cs`
  - owner lookup + owner mapping persistence
  - resolution precedence (stored mapping before email fallback)
  - email fallback mapping creation
  - unresolved owner skip behavior
  - idempotent upsert behavior (import then update on same external id)
- Profile aggregation coverage:
  - `WebApplication2.Tests/Controllers/HomeControllerProfileTests.cs`
  - current month filtering
  - fulfilled deals count
  - amount sum behavior with nullable amounts
  - owner mapping data exposure in profile model

Validation note:
- Test suite was executed successfully in a network-enabled environment.
- Example command:
  - `dotnet test WebApplication2.Tests/WebApplication2.Tests.csproj`

## Part 3: Profile expansion
Status: Implemented (initial)

Delivered in `Min profil`:
- HubSpot owner section (id, email, display name, status if mapped).
- Current month fulfilled deals count.
- Estimated total monthly amount (sum of imported amounts).
- Total monthly provision (sum of imported `saljarprovision` values).
- Current month deal list.
- Per-deal provision column in the monthly deal table.

Related files:
- `Models/UserProfileViewModel.cs`
- `Controllers/HomeController.cs`
- `Views/Home/Profile.cshtml`

## Post-scope delivery: Seller provision data pipeline and dev seeding

Status: Implemented

Delivered:
- HubSpot deal parsing now reads `saljarprovision` via configurable mapping key:
  - `HubSpot:ProvisionProperty` (default `saljarprovision`) in `appsettings.json`
  - `Services/HubSpot/HubSpotOptions.cs`
  - `Services/HubSpot/HubSpotClient.cs`
  - `Services/HubSpot/IHubSpotClient.cs`
- Imported deal storage now persists seller provision:
  - new `HubSpotDealImports.SellerProvision` (`decimal(18,2)`)
  - migration: `Migrations/20260222095205_AddSellerProvisionToHubSpotDeals.cs`
  - EF model/config: `Models/HubSpotDealImport.cs`, `Data/STLForumContext.cs`
  - sync upsert mapping: `Services/HubSpot/HubSpotSyncService.cs`
- Profile calculations now include:
  - total monthly provision
  - provision per deal row
  - files: `Controllers/HomeController.cs`, `Models/UserProfileViewModel.cs`, `Views/Home/Profile.cshtml`
- Dev superadmin preview seed script updated:
  - `scripts/seed_superadmin_preview_deals.sh`
  - seeds `SellerProvision` values (`--start-provision`, `--step-provision`)
  - seed mode now rewrites existing stored deals for the target superadmin (`OwnerUserId`) before inserting preview rows

## Deferred (Not in current scope)

- Admin UI for manual owner mapping is currently removed.

## Post-scope delivery: Fulfilled-deals leaderboard source

Status: Implemented

Delivered:
- Fulfilled HubSpot deals are persisted as leaderboard source rows in `HubSpotDealImports`.
- `HubSpotDealImports.HubSpotOwnerId` now has an FK to `HubSpotOwnerMappings.HubSpotOwnerId`.
- New migration:
  - `Migrations/20260219203000_AddHubSpotOwnerForeignKeyToDeals.cs`
  - backfills missing owner mapping rows for historical fulfilled deals before adding FK.
- Sync rules in `HubSpotSyncService`:
  - only fulfilled deals are stored,
  - deals that are later redacted/non-fulfilled are removed,
  - deals missing `hubspot_owner_id` are skipped.

Leaderboard behavior:
- Social index contest cards (top 3) are built live from fulfilled HubSpot deals.
- Social search contest cards (top 3) are built live from fulfilled HubSpot deals.
- Full contest leaderboard page (`/Social/ContestLeaderboard/{id}`) is built live from fulfilled HubSpot deals.
- Display labels are resolved from local mapped user if present, otherwise owner mapping/name/email fallback.
- Primary HubSpot team name is included in leaderboard labels.
