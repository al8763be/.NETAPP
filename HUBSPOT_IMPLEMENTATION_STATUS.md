# HubSpot Integration Status (Point of Truth)

Last updated: 2026-03-22

## Current Source Of Truth

- Deal ownership is determined exclusively by contact `saljid`.
- HubSpot owner mapping is no longer part of the live sync strategy.
- The synced seller for a deal comes from the first primary associated contact.
- If no primary contact association is marked, the first associated contact is used as fallback.

## Current Behavior

### Deal fetching and attribution
Status: Implemented

- Incremental sync fetches deals from HubSpot and enriches them with contact and line item data.
- Incremental sync now persists its cursor and only advances `LastSuccessfulSyncUtc` after the modified-since result set is fully exhausted.
- Incremental sync catch-up is intentionally bounded by a configurable recent lookback window, so very old modified deal backlog is skipped instead of being paged indefinitely.
- Rebuild/current-window sync fetches contacts by `forsaljningsdatum`, resolves associated deals, and then enriches those deals from the selected associated contact.
- Active contest window sync now persists a separate cursor per contest window so large windows continue across scheduled runs instead of restarting from page 1.
- After a full active-window sweep completes, stored rows inside that window that were not seen during the sweep are pruned as stale.
- Contact enrichment is based on a single selected associated contact per deal.
- `FulfilledDateUtc` is taken from the selected associated contact's `forsaljningsdatum`.
- Imported deal rows are stored in `HubSpotDealImports`.
- Local user mapping is resolved directly from `SaljId` to the app username/user.

Related files:
- `Services/HubSpot/HubSpotClient.cs`
- `Services/HubSpot/HubSpotSyncService.cs`
- `Services/HubSpot/HubSpotMappingService.cs`
- `Models/HubSpotDealImport.cs`

### Operational note: VPS disk growth during HubSpot catch-up
Status: Observed

- During heavy HubSpot catch-up runs on the VPS, disk usage can rise quickly even when the database size itself is moderate.
- The main observed short-term growth source was Docker app container JSON logs, driven by verbose HTTP/EF logging during sync activity.
- Additional storage growth comes from SQL Server transaction/log files and Docker build cache on the host.
- Possible mitigations to apply later:
  - reduce application log verbosity for HubSpot/EF categories
  - run the deployed app in `Production` instead of `Development`
  - configure Docker log rotation for the app container in the Dokploy compose
  - prune unused Docker build cache/images during maintenance windows
  - monitor SQL log file growth separately from app log growth

### Fulfilled and lost deal retention
Status: Implemented

- Fulfilled deals are stored when the resolved HubSpot deal stage matches the configured fulfilled statuses.
- After contact enrichment, the selected contact `kundstatus` is authoritative for fulfillment overrides.
- Deals are forced to `IsFulfilled = false` when the selected contact `kundstatus` is one of:
  - `avslag`
  - `annullerat`
  - `winback`
  - `säljare`
- Lost deals are also retained for profile/reporting when contact `kundstatus` is one of:
  - `annullerat`
  - `winback`
  - `säljare`
- `annullerad` is not treated as a retained lost status.
- `avslag` is not retained and is removed/skipped.
- Other non-fulfilled deals are removed/skipped.
- Rebuild/current-window sync now preserves retained lost deals as well as fulfilled deals.
- Every sync run also performs a cleanup sweep of stored rows:
  - retained lost statuses incorrectly stored as fulfilled are demoted to `IsFulfilled = false`
  - `avslag` rows incorrectly stored as fulfilled are removed

Related files:
- `Models/HubSpotDealStatus.cs`
- `Services/HubSpot/HubSpotClient.cs`
- `Services/HubSpot/HubSpotSyncService.cs`

### Leaderboard and contest behavior
Status: Implemented

- Contest leaderboards are built live from `HubSpotDealImports`.
- Only deals with `IsFulfilled = true` contribute to contest rankings.
- Grouping is done by `SaljId`.
- Display labels come from local `EmployeeProfile` data where available.
- Cancelled/lost deals do not affect leaderboard counts.
- Known mismatch to fix later:
  - live leaderboard queries use contest window `FulfilledDateUtc >= StartDate.Date` and `< EndDate.Date + 1 day`
  - stored `ContestEntries` recalculation currently uses `FulfilledDateUtc >= StartDate` and `<= EndDate`
  - this can create end-date boundary differences between live leaderboard output and recalculated stored contest entries

Related files:
- `Controllers/SocialController.cs`
- `Models/ContestEntry.cs`
- `Views/Social/Index.cshtml`
- `Views/Social/ContestLeaderboard.cshtml`

### Profile behavior
Status: Implemented

- `/Home/Profile` filters deals by the logged-in user’s username matched against `SaljId`.
- The page supports current month and previous month views.
- The fulfilled section shows:
  - fulfilled deal count
  - fulfilled amount total
  - deal rows with contact details
- The lost section shows retained lost deals for the same selected month.
- Row expansion shows deal id, deal name, and imported line items.

Related files:
- `Controllers/HomeController.cs`
- `Models/UserProfileViewModel.cs`
- `Views/Home/Profile.cshtml`
- `Views/Home/_HubSpotDealTable.cshtml`

### Line-item persistence and backfill
Status: Implemented and validated

- On March 22, 2026, a live regression was confirmed where many March 2026 `HubSpotDealImports` rows were stored with `LineItemsJson = NULL` even though the corresponding HubSpot deals had associated line items.
- Two concrete causes were identified:
  - embedded deal associations from HubSpot used `associations["line items"]` while the client only parsed `associations["line_items"]`
  - the line-item batch read path was sending oversized `line_items/batch/read` requests, which caused HubSpot `400` failures during hydration/backfill
- The client now:
  - accepts both `line_items` and `line items` association keys
  - batch-reads deal to line-item associations through `/crm/v4/associations/deals/line_items/batch/read`
  - chunks `/crm/v3/objects/line_items/batch/read` requests so large backfills do not fail
- A non-destructive maintenance command now exists:
  - `--hubspot-backfill-line-items`
  - this updates only `HubSpotDealImports.LineItemsJson`
  - it does not clear `HubSpotDealImports`
  - it does not clear `ContestEntries`
  - it does not modify incremental sync cursor/watermark state
- Verified in dev on March 22, 2026 with:
  - `docker exec -it netapp-dev-app dotnet run --project WebApplication2.csproj -- --hubspot-backfill-line-items --from 2026-03-01 --to 2026-03-31`
- Verified VPS/Dokploy container command:
  - `docker exec -it intranet-app-amftl3-app-1 dotnet WebApplication2.dll --hubspot-backfill-line-items --from 2026-03-01 --to 2026-03-31`
- Observed dev result on March 22, 2026:
  - `Candidates=446`
  - `Updated=442`
  - `Skipped=4`
- The 4 remaining March 2026 rows without stored line items after backfill were:
  - `494473656520`
  - `494058191040`
  - `494058957046`
  - `486201035994`
- At least some of those remaining rows return no line-item associations from HubSpot, so the remaining nulls appear to be upstream data gaps rather than a persistence bug.

Related files:
- `Services/HubSpot/HubSpotClient.cs`
- `Services/HubSpot/HubSpotSyncService.cs`
- `Services/HubSpot/IHubSpotClient.cs`
- `Services/HubSpot/IHubSpotSyncService.cs`
- `Program.cs`

### Dev superadmin preview seed behavior
Status: Observed limitation

- `scripts/seed_superadmin_preview_deals.sh` seeds preview `HubSpotDealImports` rows with `SaljId = <superadmin username>`, currently `devsuperadmin`.
- Those preview rows are intended to appear on `/Home/Profile` because profile filtering matches the logged-in username directly against `SaljId`.
- Preview rows are not protected from the active-window stale-row pruning logic.
- If a seeded preview row has `FulfilledDateUtc` inside an active contest window and is not re-seen by a later HubSpot sweep, it is treated as stale and removed.
- This means seeded preview deals can disappear automatically after scheduled sync runs even though the profile query itself is still correct.
- The current prune logic assumes all rows inside the synced contest window are sync-owned HubSpot rows, which is not true for locally seeded preview data.

Related files:
- `scripts/seed_superadmin_preview_deals.sh`
- `Services/HubSpot/HubSpotSyncService.cs`
- `Controllers/HomeController.cs`

## Tests
Status: Implemented and validated

- HubSpot sync tests cover:
  - direct `SaljId` ownership mapping
  - skipping deals without valid `SaljId`
  - idempotent upserts
  - redacted/non-fulfilled removal
  - retained lost statuses
  - fulfillment override for `avslag`, `annullerat`, `winback`, and `säljare`
  - stale-row cleanup/demotion for excluded fulfilled statuses
  - excluded `annullerad`
  - rebuild retention of fulfilled and allowed lost deals
- HubSpot client tests cover:
  - fulfilled-stage resolution
  - primary associated contact selection
  - search-window inclusion of fulfilled and allowed lost deals
- Profile tests cover:
  - current and previous month filtering
  - fulfilled aggregation
  - lost-deal rendering set for the selected month

Validated command:
- `dotnet test WebApplication2.Tests/WebApplication2.Tests.csproj --filter "FullyQualifiedName~HubSpot|FullyQualifiedName~HomeControllerProfileTests"`

## Removed From Live Strategy

- `HubSpotOwnerMappings`
- `HubSpotDealImports.HubSpotOwnerId`
- HubSpot owner lookup as a source of local-user resolution
- Owner/team fallback labeling for leaderboard identity

## Schema Change

- Migration added to remove obsolete owner-mapping schema:
  - `Migrations/20260317161219_RemoveHubSpotOwnerMappingStrategy.cs`
