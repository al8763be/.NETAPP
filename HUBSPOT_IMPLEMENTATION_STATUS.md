# HubSpot Integration Status (Point of Truth)

Last updated: 2026-03-18

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
