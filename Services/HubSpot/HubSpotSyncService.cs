using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebApplication2.Data;
using WebApplication2.Models;

namespace WebApplication2.Services.HubSpot
{
    public class HubSpotSyncService : IHubSpotSyncService
    {
        private readonly STLForumContext _context;
        private readonly IHubSpotClient _hubSpotClient;
        private readonly IHubSpotMappingService _mappingService;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly HubSpotOptions _options;
        private readonly ILogger<HubSpotSyncService> _logger;

        public HubSpotSyncService(
            STLForumContext context,
            IHubSpotClient hubSpotClient,
            IHubSpotMappingService mappingService,
            UserManager<IdentityUser> userManager,
            IOptions<HubSpotOptions> options,
            ILogger<HubSpotSyncService> logger)
        {
            _context = context;
            _hubSpotClient = hubSpotClient;
            _mappingService = mappingService;
            _userManager = userManager;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<HubSpotSyncRunResult> RunIncrementalSyncAsync(CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return new HubSpotSyncRunResult
                {
                    Succeeded = true,
                    Message = "HubSpot sync is disabled in configuration."
                };
            }

            var run = new HubSpotSyncRun
            {
                StartedUtc = DateTime.UtcNow,
                Status = "Started"
            };

            _context.HubSpotSyncRuns.Add(run);
            await _context.SaveChangesAsync(cancellationToken);

            try
            {
                var syncState = await _context.HubSpotSyncStates
                    .FirstOrDefaultAsync(s => s.IntegrationName == "HubSpotDeals", cancellationToken);

                if (syncState == null)
                {
                    syncState = new HubSpotSyncState
                    {
                        IntegrationName = "HubSpotDeals"
                    };
                    _context.HubSpotSyncStates.Add(syncState);
                }

                syncState.LastAttemptUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                var isInitialBackfill = !syncState.LastSuccessfulSyncUtc.HasValue;
                var modifiedSinceUtc = isInitialBackfill ? null : syncState.LastSuccessfulSyncUtc;
                var cursor = isInitialBackfill ? syncState.LastCursor : null;
                var pageCount = 0;
                var reachedEnd = false;

                while (pageCount < _options.MaxPagesPerRun)
                {
                    pageCount++;
                    var page = await _hubSpotClient.GetFulfilledDealsAsync(
                        modifiedSinceUtc,
                        cursor,
                        _options.PageSize,
                        cancellationToken);

                    run.DealsFetched += page.Deals.Count;

                    foreach (var deal in page.Deals)
                    {
                        var upsertResult = await UpsertDealAsync(deal, cancellationToken);
                        run.DealsImported += upsertResult.Imported;
                        run.DealsUpdated += upsertResult.Updated;
                        run.DealsSkipped += upsertResult.Skipped;
                    }

                    cursor = page.NextCursor;
                    if (string.IsNullOrWhiteSpace(cursor))
                    {
                        reachedEnd = true;
                        break;
                    }
                }

                var activeContestSync = await SyncActiveContestWindowsAsync(cancellationToken);
                run.DealsFetched += activeContestSync.Fetched;
                run.DealsImported += activeContestSync.Imported;
                run.DealsUpdated += activeContestSync.Updated;
                run.DealsSkipped += activeContestSync.Skipped;

                await RecalculateActiveContestEntriesAsync(cancellationToken);
                run.Status = "Succeeded";
                run.FinishedUtc = DateTime.UtcNow;

                if (isInitialBackfill)
                {
                    if (reachedEnd)
                    {
                        syncState.LastSuccessfulSyncUtc = DateTime.UtcNow;
                        syncState.LastCursor = null;
                    }
                    else
                    {
                        // Continue historical backfill next run.
                        syncState.LastCursor = cursor;
                    }
                }
                else
                {
                    syncState.LastSuccessfulSyncUtc = DateTime.UtcNow;
                    // Incremental runs always restart from cursor null.
                    syncState.LastCursor = null;
                }
                syncState.LastError = null;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "HubSpot sync run {RunId} completed. Deals fetched: {DealsFetched}",
                    run.Id,
                    run.DealsFetched);

                return new HubSpotSyncRunResult
                {
                    Succeeded = true,
                    DealsFetched = run.DealsFetched,
                    DealsImported = run.DealsImported,
                    DealsUpdated = run.DealsUpdated,
                    DealsSkipped = run.DealsSkipped,
                    Message = "HubSpot sync scaffold executed successfully."
                };
            }
            catch (Exception ex)
            {
                run.Status = "Failed";
                run.ErrorMessage = ex.Message;
                run.FinishedUtc = DateTime.UtcNow;

                var syncState = await _context.HubSpotSyncStates
                    .FirstOrDefaultAsync(s => s.IntegrationName == "HubSpotDeals", cancellationToken);
                if (syncState != null)
                {
                    syncState.LastError = ex.Message;
                }

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogError(ex, "HubSpot sync run {RunId} failed", run.Id);
                return new HubSpotSyncRunResult
                {
                    Succeeded = false,
                    Message = ex.Message
                };
            }
        }

        private async Task<(int Imported, int Updated, int Skipped)> UpsertDealAsync(
            HubSpotDealRecord deal,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(deal.ExternalDealId))
            {
                return (0, 0, 1);
            }

            var existing = await _context.HubSpotDealImports
                .FirstOrDefaultAsync(d => d.ExternalDealId == deal.ExternalDealId, cancellationToken);

            // Deal was previously fulfilled but has now moved out of fulfilled state.
            // Remove it so rankings are recalculated from only currently fulfilled deals.
            if (!deal.IsFulfilled || !deal.FulfilledDateUtc.HasValue)
            {
                if (existing == null)
                {
                    return (0, 0, 1);
                }

                _context.HubSpotDealImports.Remove(existing);
                await _context.SaveChangesAsync(cancellationToken);
                return (0, 1, 0);
            }

            // Persist fulfilled deals only when owner id is available so deal rows are tied to HubSpot owner mapping.
            if (string.IsNullOrWhiteSpace(deal.OwnerId))
            {
                return (0, 0, 1);
            }

            var ownerEmail = deal.OwnerEmail;
            HubSpotOwnerRecord? ownerRecord = null;
            ownerRecord = await _hubSpotClient.GetOwnerByOwnerIdAsync(deal.OwnerId, cancellationToken);
            if (string.IsNullOrWhiteSpace(ownerEmail))
            {
                ownerEmail = ownerRecord?.Email;
            }

            var ownerUser = await ResolveOwnerUserAsync(
                deal.OwnerId,
                ownerEmail ?? string.Empty,
                ownerRecord,
                cancellationToken);

            var normalizedOwnerEmail = ownerEmail?.Trim() ?? string.Empty;

            if (existing == null)
            {
                _context.HubSpotDealImports.Add(new HubSpotDealImport
                {
                    ExternalDealId = deal.ExternalDealId,
                    DealName = deal.DealName,
                    HubSpotOwnerId = deal.OwnerId,
                    OwnerEmail = normalizedOwnerEmail,
                    OwnerUserId = ownerUser?.Id,
                    FulfilledDateUtc = deal.FulfilledDateUtc.Value,
                    Amount = deal.Amount,
                    SellerProvision = deal.SellerProvision,
                    CurrencyCode = deal.CurrencyCode,
                    DealStage = deal.DealStage,
                    HubSpotLastModifiedUtc = deal.LastModifiedUtc,
                    PayloadHash = deal.PayloadHash,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                });

                await _context.SaveChangesAsync(cancellationToken);
                return (1, 0, 0);
            }

            existing.DealName = deal.DealName;
            existing.HubSpotOwnerId = deal.OwnerId;
            existing.OwnerEmail = normalizedOwnerEmail;
            existing.OwnerUserId = ownerUser?.Id;
            existing.FulfilledDateUtc = deal.FulfilledDateUtc.Value;
            existing.Amount = deal.Amount;
            existing.SellerProvision = deal.SellerProvision;
            existing.CurrencyCode = deal.CurrencyCode;
            existing.DealStage = deal.DealStage;
            existing.HubSpotLastModifiedUtc = deal.LastModifiedUtc;
            existing.PayloadHash = deal.PayloadHash;
            existing.LastSeenUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            return (0, 1, 0);
        }

        private async Task<(int Fetched, int Imported, int Updated, int Skipped)> SyncActiveContestWindowsAsync(
            CancellationToken cancellationToken)
        {
            var activeContests = await _context.Contests
                .AsNoTracking()
                .Where(c => c.IsActive && c.EndDate > DateTime.Now)
                .ToListAsync(cancellationToken);

            _logger.LogDebug("Active contest window sync candidate count: {ContestCount}", activeContests.Count);

            if (!activeContests.Any())
            {
                return (0, 0, 0, 0);
            }

            var fetched = 0;
            var imported = 0;
            var updated = 0;
            var skipped = 0;

            foreach (var contest in activeContests)
            {
                var contestStartUtc = DateTime.SpecifyKind(contest.StartDate, DateTimeKind.Local).ToUniversalTime();
                var contestEndUtc = DateTime.SpecifyKind(contest.EndDate, DateTimeKind.Local).ToUniversalTime();

                string? cursor = null;
                var pageCount = 0;

                while (pageCount < _options.MaxPagesPerRun)
                {
                    pageCount++;

                    var page = await _hubSpotClient.SearchFulfilledDealsByClosedDateRangeAsync(
                        contestStartUtc,
                        contestEndUtc,
                        cursor,
                        _options.PageSize,
                        cancellationToken);

                    fetched += page.Deals.Count;

                    foreach (var deal in page.Deals)
                    {
                        var upsertResult = await UpsertDealAsync(deal, cancellationToken);
                        imported += upsertResult.Imported;
                        updated += upsertResult.Updated;
                        skipped += upsertResult.Skipped;
                    }

                    cursor = page.NextCursor;
                    if (string.IsNullOrWhiteSpace(cursor))
                    {
                        break;
                    }
                }
            }

            return (fetched, imported, updated, skipped);
        }

        private async Task<IdentityUser?> ResolveOwnerUserAsync(
            string? hubSpotOwnerId,
            string ownerEmail,
            HubSpotOwnerRecord? ownerRecord,
            CancellationToken cancellationToken)
        {
            HubSpotOwnerMapping? mapping = null;

            if (!string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                mapping = await _context.HubSpotOwnerMappings
                    .FirstOrDefaultAsync(m => m.HubSpotOwnerId == hubSpotOwnerId, cancellationToken);
            }

            IdentityUser? ownerUser = null;

            // Prefer explicit persisted mapping when available.
            if (!string.IsNullOrWhiteSpace(mapping?.OwnerUserId))
            {
                ownerUser = await _userManager.FindByIdAsync(mapping.OwnerUserId);
            }

            // Fallback: infer local employee user from owner email format.
            if (ownerUser == null && _mappingService.TryExtractEmployeeUsername(ownerEmail, out var employeeUsername))
            {
                ownerUser = await _userManager.FindByNameAsync(employeeUsername);
            }

            await UpsertOwnerMappingAsync(
                mapping,
                hubSpotOwnerId,
                ownerEmail,
                ownerRecord,
                ownerUser,
                cancellationToken);

            return ownerUser;
        }

        private async Task UpsertOwnerMappingAsync(
            HubSpotOwnerMapping? existingMapping,
            string? hubSpotOwnerId,
            string ownerEmail,
            HubSpotOwnerRecord? ownerRecord,
            IdentityUser? ownerUser,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                return;
            }

            var mapping = existingMapping ?? new HubSpotOwnerMapping
            {
                HubSpotOwnerId = hubSpotOwnerId
            };

            mapping.HubSpotOwnerEmail = FirstNonEmpty(ownerRecord?.Email, ownerEmail, mapping.HubSpotOwnerEmail);

            if (!string.IsNullOrWhiteSpace(ownerRecord?.FirstName))
            {
                mapping.HubSpotFirstName = ownerRecord.FirstName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(ownerRecord?.LastName))
            {
                mapping.HubSpotLastName = ownerRecord.LastName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(ownerRecord?.PrimaryTeamName))
            {
                mapping.HubSpotPrimaryTeamName = ownerRecord.PrimaryTeamName.Trim();
            }

            if (ownerRecord?.TeamNames != null && ownerRecord.TeamNames.Count > 0)
            {
                mapping.HubSpotTeamNames = string.Join(" | ", ownerRecord.TeamNames.Where(t => !string.IsNullOrWhiteSpace(t)));
            }

            if (ownerRecord != null)
            {
                mapping.IsArchived = ownerRecord.IsArchived;
            }

            mapping.LastSeenUtc = DateTime.UtcNow;
            mapping.LastOwnerSyncUtc = DateTime.UtcNow;

            // Keep stable explicit mapping if already present; otherwise auto-link when confidently resolved.
            if (string.IsNullOrWhiteSpace(mapping.OwnerUserId) && ownerUser != null)
            {
                mapping.OwnerUserId = ownerUser.Id;
            }

            mapping.OwnerUsername = ownerUser?.UserName ?? mapping.OwnerUsername;

            if (existingMapping == null)
            {
                _context.HubSpotOwnerMappings.Add(mapping);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private async Task RecalculateActiveContestEntriesAsync(CancellationToken cancellationToken)
        {
            var activeContests = await _context.Contests
                .Where(c => c.IsActive && c.EndDate > DateTime.Now)
                .ToListAsync(cancellationToken);

            if (!activeContests.Any())
            {
                return;
            }

            var activeContestIds = activeContests.Select(c => c.Id).ToList();

            var existingEntries = await _context.ContestEntries
                .Where(ce => activeContestIds.Contains(ce.ContestId))
                .ToListAsync(cancellationToken);

            if (existingEntries.Any())
            {
                _context.ContestEntries.RemoveRange(existingEntries);
                await _context.SaveChangesAsync(cancellationToken);
            }

            foreach (var contest in activeContests)
            {
                var contestStartUtc = DateTime.SpecifyKind(contest.StartDate, DateTimeKind.Local).ToUniversalTime();
                var contestEndUtc = DateTime.SpecifyKind(contest.EndDate, DateTimeKind.Local).ToUniversalTime();

                var groupedDeals = await _context.HubSpotDealImports
                    .Where(d =>
                        d.FulfilledDateUtc >= contestStartUtc &&
                        d.FulfilledDateUtc <= contestEndUtc &&
                        (!string.IsNullOrWhiteSpace(d.HubSpotOwnerId) || !string.IsNullOrWhiteSpace(d.OwnerEmail)))
                    .GroupBy(d => new
                    {
                        d.HubSpotOwnerId,
                        d.OwnerEmail
                    })
                    .Select(g => new
                    {
                        HubSpotOwnerId = g.Key.HubSpotOwnerId,
                        OwnerEmail = g.Key.OwnerEmail,
                        DealsCount = g.Count()
                    })
                    .ToListAsync(cancellationToken);

                if (!groupedDeals.Any())
                {
                    continue;
                }

                var ownerIds = groupedDeals
                    .Select(g => NormalizeOwnerId(g.HubSpotOwnerId))
                    .Where(id => id != null)
                    .Select(id => id!)
                    .Distinct()
                    .ToList();

                var normalizedOwnerEmails = groupedDeals
                    .Select(g => NormalizeOwnerEmail(g.OwnerEmail))
                    .Where(email => email != null)
                    .Select(email => email!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var mappings = await _context.HubSpotOwnerMappings
                    .AsNoTracking()
                    .Where(m =>
                        ownerIds.Contains(m.HubSpotOwnerId) ||
                        (m.HubSpotOwnerEmail != null && normalizedOwnerEmails.Contains(m.HubSpotOwnerEmail.ToLower())))
                    .ToListAsync(cancellationToken);

                var groupedByOwner = new Dictionary<string, (string? HubSpotOwnerId, string? OwnerEmail, HubSpotOwnerMapping? Mapping, int DealsCount)>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var grouped in groupedDeals)
                {
                    var mapping = ResolveOwnerMapping(mappings, grouped.HubSpotOwnerId, grouped.OwnerEmail);

                    var ownerKey = BuildOwnerAggregationKey(grouped.HubSpotOwnerId, grouped.OwnerEmail, mapping);
                    if (groupedByOwner.TryGetValue(ownerKey, out var existing))
                    {
                        groupedByOwner[ownerKey] = (
                            HubSpotOwnerId: FirstNonEmptyTrim(existing.HubSpotOwnerId, grouped.HubSpotOwnerId, mapping?.HubSpotOwnerId),
                            OwnerEmail: FirstNonEmptyTrim(existing.OwnerEmail, grouped.OwnerEmail, mapping?.HubSpotOwnerEmail),
                            Mapping: existing.Mapping ?? mapping,
                            DealsCount: existing.DealsCount + grouped.DealsCount
                        );
                        continue;
                    }

                    groupedByOwner[ownerKey] = (
                        HubSpotOwnerId: FirstNonEmptyTrim(grouped.HubSpotOwnerId, mapping?.HubSpotOwnerId),
                        OwnerEmail: FirstNonEmptyTrim(grouped.OwnerEmail, mapping?.HubSpotOwnerEmail),
                        Mapping: mapping,
                        DealsCount: grouped.DealsCount
                    );
                }

                foreach (var groupedOwner in groupedByOwner.Values)
                {
                    var displayLabel = BuildContestDisplayLabel(
                        groupedOwner.HubSpotOwnerId,
                        groupedOwner.OwnerEmail,
                        groupedOwner.Mapping);
                    if (string.IsNullOrWhiteSpace(displayLabel))
                    {
                        displayLabel = "Okänd owner";
                    }

                    displayLabel = displayLabel.Trim();
                    if (displayLabel.Length > 50)
                    {
                        displayLabel = displayLabel[..50];
                    }

                    _context.ContestEntries.Add(new ContestEntry
                    {
                        ContestId = contest.Id,
                        UserId = groupedOwner.Mapping?.OwnerUserId,
                        EmployeeNumber = displayLabel,
                        DealsCount = groupedOwner.DealsCount,
                        UpdatedDate = DateTime.Now
                    });
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        private static HubSpotOwnerMapping? ResolveOwnerMapping(
            IEnumerable<HubSpotOwnerMapping> mappings,
            string? hubSpotOwnerId,
            string? ownerEmail)
        {
            if (!string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                var normalizedOwnerId = NormalizeOwnerId(hubSpotOwnerId);
                var byOwnerId = mappings.FirstOrDefault(m => m.HubSpotOwnerId == normalizedOwnerId);
                if (byOwnerId != null)
                {
                    return byOwnerId;
                }
            }

            if (!string.IsNullOrWhiteSpace(ownerEmail))
            {
                var normalizedEmail = NormalizeOwnerEmail(ownerEmail);
                return mappings.FirstOrDefault(m =>
                    !string.IsNullOrWhiteSpace(m.HubSpotOwnerEmail) &&
                    m.HubSpotOwnerEmail.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static string BuildContestDisplayLabel(
            string? hubSpotOwnerId,
            string? ownerEmail,
            HubSpotOwnerMapping? mapping)
        {
            var teamSuffix = string.Empty;
            if (!string.IsNullOrWhiteSpace(mapping?.HubSpotPrimaryTeamName))
            {
                teamSuffix = $" ({mapping.HubSpotPrimaryTeamName})";
            }

            var mappedName = $"{mapping?.HubSpotFirstName} {mapping?.HubSpotLastName}".Trim();
            if (!string.IsNullOrWhiteSpace(mappedName))
            {
                return $"{mappedName}{teamSuffix}";
            }

            if (!string.IsNullOrWhiteSpace(mapping?.HubSpotOwnerEmail))
            {
                return $"{mapping.HubSpotOwnerEmail.Trim()}{teamSuffix}";
            }

            if (!string.IsNullOrWhiteSpace(ownerEmail))
            {
                return $"{ownerEmail.Trim()}{teamSuffix}";
            }

            if (!string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                return $"HubSpot-{hubSpotOwnerId.Trim()}{teamSuffix}";
            }

            return "Okänd owner";
        }

        private static string BuildOwnerAggregationKey(
            string? hubSpotOwnerId,
            string? ownerEmail,
            HubSpotOwnerMapping? mapping)
        {
            var canonicalOwnerId = FirstNonEmptyTrim(hubSpotOwnerId, mapping?.HubSpotOwnerId);
            if (!string.IsNullOrWhiteSpace(canonicalOwnerId))
            {
                return $"id:{canonicalOwnerId}";
            }

            var normalizedEmail = NormalizeOwnerEmail(FirstNonEmptyTrim(ownerEmail, mapping?.HubSpotOwnerEmail));
            if (!string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return $"email:{normalizedEmail}";
            }

            return $"unknown:{hubSpotOwnerId?.Trim()}:{ownerEmail?.Trim()}";
        }

        private static string? NormalizeOwnerId(string? hubSpotOwnerId)
        {
            if (string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                return null;
            }

            return hubSpotOwnerId.Trim();
        }

        private static string? NormalizeOwnerEmail(string? ownerEmail)
        {
            if (string.IsNullOrWhiteSpace(ownerEmail))
            {
                return null;
            }

            return ownerEmail.Trim().ToLower();
        }

        private static string? FirstNonEmptyTrim(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }
    }
}
