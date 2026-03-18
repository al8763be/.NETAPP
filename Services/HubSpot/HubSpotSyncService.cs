using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using WebApplication2.Data;
using WebApplication2.Models;

namespace WebApplication2.Services.HubSpot
{
    public class HubSpotSyncService : IHubSpotSyncService
    {
        private const string IncrementalSyncStateName = "HubSpotDeals";
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
                var syncState = await GetOrCreateSyncStateAsync(IncrementalSyncStateName, cancellationToken);
                syncState.LastAttemptUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                var modifiedSinceUtc = syncState.LastSuccessfulSyncUtc;
                var cursor = syncState.LastCursor;
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

                await NormalizeStoredDealsByKundstatusAsync(cancellationToken);
                await RecalculateActiveContestEntriesAsync(cancellationToken);
                run.Status = "Succeeded";
                run.FinishedUtc = DateTime.UtcNow;

                if (reachedEnd)
                {
                    // Use the run start as the incremental watermark so updates that land mid-run
                    // are picked up again on the next pass instead of being skipped.
                    syncState.LastSuccessfulSyncUtc = run.StartedUtc;
                    syncState.LastCursor = null;
                }
                else
                {
                    syncState.LastCursor = cursor;
                }

                syncState.LastError = null;

                await _context.SaveChangesAsync(cancellationToken);

                if (!reachedEnd)
                {
                    _logger.LogWarning(
                        "HubSpot incremental sync run {RunId} hit MaxPagesPerRun={MaxPagesPerRun} and will continue from cursor {Cursor} next run.",
                        run.Id,
                        _options.MaxPagesPerRun,
                        cursor ?? "<none>");
                }

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
                    .FirstOrDefaultAsync(s => s.IntegrationName == IncrementalSyncStateName, cancellationToken);
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

        public async Task<HubSpotSyncRunResult> RebuildCurrentMonthOnlyAsync(CancellationToken cancellationToken = default)
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
                var nowUtc = DateTime.UtcNow;
                var monthStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var monthEndUtc = monthStartUtc.AddMonths(1).AddTicks(-1);

                await ClearHubSpotImportDataAsync(cancellationToken);

                string? cursor = null;
                while (true)
                {
                    var page = await _hubSpotClient.SearchFulfilledDealsByClosedDateRangeAsync(
                        monthStartUtc,
                        monthEndUtc,
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
                        break;
                    }
                }

                await NormalizeStoredDealsByKundstatusAsync(cancellationToken);
                await RecalculateActiveContestEntriesAsync(cancellationToken);

                var syncState = await _context.HubSpotSyncStates
                    .FirstOrDefaultAsync(s => s.IntegrationName == "HubSpotDeals", cancellationToken);
                if (syncState != null)
                {
                    syncState.LastSuccessfulSyncUtc = DateTime.UtcNow;
                    syncState.LastCursor = null;
                    syncState.LastError = null;
                    syncState.LastAttemptUtc = DateTime.UtcNow;
                }

                run.Status = "Succeeded";
                run.FinishedUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                return new HubSpotSyncRunResult
                {
                    Succeeded = true,
                    DealsFetched = run.DealsFetched,
                    DealsImported = run.DealsImported,
                    DealsUpdated = run.DealsUpdated,
                    DealsSkipped = run.DealsSkipped,
                    Message = "HubSpot month rebuild completed."
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

                _logger.LogError(ex, "HubSpot month rebuild failed for run {RunId}", run.Id);
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

            var contactKundstatus = NormalizeOptionalText(deal.ContactKundstatus, 128);
            var shouldRetainLostDeal = HubSpotDealStatus.IsLostKundstatus(contactKundstatus);

            if ((!deal.IsFulfilled && !shouldRetainLostDeal) || !deal.FulfilledDateUtc.HasValue)
            {
                if (existing == null)
                {
                    return (0, 0, 1);
                }

                _context.HubSpotDealImports.Remove(existing);
                await _context.SaveChangesAsync(cancellationToken);
                return (0, 1, 0);
            }

            var normalizedSaljId = NormalizeSaljId(deal.SaljId);
            if (normalizedSaljId == null)
            {
                return (0, 0, 1);
            }

            var ownerUser = await _userManager.FindByNameAsync(normalizedSaljId);
            var normalizedOwnerEmail = deal.OwnerEmail?.Trim() ?? string.Empty;
            var contactFirstName = NormalizeOptionalText(deal.ContactFirstName, 128);
            var contactPhoneNumber = NormalizeOptionalText(deal.ContactPhoneNumber, 64);
            var lineItemsJson = SerializeLineItems(deal.LineItems);

            if (existing == null)
            {
                _context.HubSpotDealImports.Add(new HubSpotDealImport
                {
                    ExternalDealId = deal.ExternalDealId,
                    DealName = deal.DealName,
                    SaljId = normalizedSaljId,
                    OwnerEmail = normalizedOwnerEmail,
                    OwnerUserId = ownerUser?.Id,
                    IsFulfilled = deal.IsFulfilled,
                    FulfilledDateUtc = deal.FulfilledDateUtc.Value,
                    Amount = deal.Amount,
                    SellerProvision = deal.SellerProvision,
                    CurrencyCode = deal.CurrencyCode,
                    DealStage = deal.DealStage,
                    HubSpotLastModifiedUtc = deal.LastModifiedUtc,
                    PayloadHash = deal.PayloadHash,
                    ContactFirstName = contactFirstName,
                    ContactPhoneNumber = contactPhoneNumber,
                    ContactKundstatus = contactKundstatus,
                    LineItemsJson = lineItemsJson,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                });

                await _context.SaveChangesAsync(cancellationToken);
                return (1, 0, 0);
            }

            existing.DealName = deal.DealName;
            existing.SaljId = normalizedSaljId;
            existing.OwnerEmail = normalizedOwnerEmail;
            existing.OwnerUserId = ownerUser?.Id;
            existing.IsFulfilled = deal.IsFulfilled;
            existing.FulfilledDateUtc = deal.FulfilledDateUtc.Value;
            existing.Amount = deal.Amount;
            existing.SellerProvision = deal.SellerProvision;
            existing.CurrencyCode = deal.CurrencyCode;
            existing.DealStage = deal.DealStage;
            existing.HubSpotLastModifiedUtc = deal.LastModifiedUtc;
            existing.PayloadHash = deal.PayloadHash;
            existing.ContactFirstName = contactFirstName;
            existing.ContactPhoneNumber = contactPhoneNumber;
            existing.ContactKundstatus = contactKundstatus;
            existing.LineItemsJson = lineItemsJson;
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
                var syncState = await GetOrCreateSyncStateAsync(GetContestSyncStateName(contest), cancellationToken);
                syncState.LastAttemptUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                var cursor = syncState.LastCursor;
                var pageCount = 0;
                var reachedEnd = false;

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
                        reachedEnd = true;
                        break;
                    }
                }

                if (reachedEnd)
                {
                    syncState.LastSuccessfulSyncUtc = DateTime.UtcNow;
                    syncState.LastCursor = null;
                }
                else
                {
                    syncState.LastCursor = cursor;
                    _logger.LogWarning(
                        "HubSpot contest-window sync for contest {ContestId} hit MaxPagesPerRun={MaxPagesPerRun} and will continue from cursor {Cursor} next run.",
                        contest.Id,
                        _options.MaxPagesPerRun,
                        cursor ?? "<none>");
                }

                syncState.LastError = null;
                await _context.SaveChangesAsync(cancellationToken);
            }

            return (fetched, imported, updated, skipped);
        }

        private async Task<HubSpotSyncState> GetOrCreateSyncStateAsync(
            string integrationName,
            CancellationToken cancellationToken)
        {
            var syncState = await _context.HubSpotSyncStates
                .FirstOrDefaultAsync(s => s.IntegrationName == integrationName, cancellationToken);

            if (syncState != null)
            {
                return syncState;
            }

            syncState = new HubSpotSyncState
            {
                IntegrationName = integrationName
            };

            _context.HubSpotSyncStates.Add(syncState);
            return syncState;
        }

        private static string GetContestSyncStateName(Contest contest)
            => $"HubSpotDealsContest:{contest.Id}:{contest.StartDate:yyyyMMdd}:{contest.EndDate:yyyyMMdd}";

        private string? NormalizeSaljId(string? saljId)
        {
            if (string.IsNullOrWhiteSpace(saljId))
            {
                return null;
            }

            var trimmed = saljId.Trim();
            if (_mappingService.IsValidEmployeeUsername(trimmed))
            {
                return trimmed;
            }

            return null;
        }

        private static string? NormalizeOptionalText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            return trimmed[..maxLength];
        }

        private static string? SerializeLineItems(IReadOnlyCollection<HubSpotDealLineItemRecord> lineItems)
        {
            if (lineItems == null || lineItems.Count == 0)
            {
                return null;
            }

            var normalized = lineItems
                .Select(item => new StoredDealLineItem
                {
                    LineItemId = NormalizeOptionalText(item.LineItemId, 128),
                    Name = NormalizeOptionalText(item.Name, 512),
                    Quantity = item.Quantity,
                    Price = item.Price,
                    Amount = item.Amount,
                    Sku = NormalizeOptionalText(item.Sku, 128)
                })
                .ToList();

            if (normalized.Count == 0)
            {
                return null;
            }

            return JsonSerializer.Serialize(normalized);
        }

        private sealed class StoredDealLineItem
        {
            public string? LineItemId { get; set; }
            public string? Name { get; set; }
            public decimal? Quantity { get; set; }
            public decimal? Price { get; set; }
            public decimal? Amount { get; set; }
            public string? Sku { get; set; }
        }

        private async Task ClearHubSpotImportDataAsync(CancellationToken cancellationToken)
        {
            var dealRows = await _context.HubSpotDealImports.ToListAsync(cancellationToken);
            if (dealRows.Count > 0)
            {
                _context.HubSpotDealImports.RemoveRange(dealRows);
            }

            var contestEntries = await _context.ContestEntries.ToListAsync(cancellationToken);
            if (contestEntries.Count > 0)
            {
                _context.ContestEntries.RemoveRange(contestEntries);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        private async Task NormalizeStoredDealsByKundstatusAsync(CancellationToken cancellationToken)
        {
            var storedDeals = await _context.HubSpotDealImports
                .ToListAsync(cancellationToken);

            if (storedDeals.Count == 0)
            {
                return;
            }

            var dealsToRemove = new List<HubSpotDealImport>();
            var dealsToNormalize = new List<HubSpotDealImport>();

            foreach (var deal in storedDeals)
            {
                var shouldBeFulfilled = HubSpotDealStatus.IsFulfilledKundstatus(deal.ContactKundstatus);
                var shouldRetainLost = HubSpotDealStatus.IsLostKundstatus(deal.ContactKundstatus);

                if (!shouldBeFulfilled && !shouldRetainLost)
                {
                    dealsToRemove.Add(deal);
                    continue;
                }

                if (deal.IsFulfilled != shouldBeFulfilled)
                {
                    deal.IsFulfilled = shouldBeFulfilled;
                    dealsToNormalize.Add(deal);
                }
            }

            if (dealsToRemove.Count > 0)
            {
                _context.HubSpotDealImports.RemoveRange(dealsToRemove);
            }

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Normalized {NormalizedCount} HubSpot deals by kundstatus and removed {RemovedCount} non-qualifying HubSpot deals.",
                dealsToNormalize.Count,
                dealsToRemove.Count);
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
                        d.IsFulfilled &&
                        d.FulfilledDateUtc >= contestStartUtc &&
                        d.FulfilledDateUtc <= contestEndUtc &&
                        !string.IsNullOrWhiteSpace(d.SaljId))
                    .GroupBy(d => d.SaljId)
                    .Select(g => new
                    {
                        SaljId = g.Key,
                        DealsCount = g.Count()
                    })
                    .ToListAsync(cancellationToken);

                if (!groupedDeals.Any())
                {
                    continue;
                }

                var dealsByEmployeeNumber = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var grouped in groupedDeals)
                {
                    var saljId = NormalizeSaljId(grouped.SaljId);
                    if (saljId == null)
                    {
                        continue;
                    }

                    if (dealsByEmployeeNumber.TryGetValue(saljId, out var existingCount))
                    {
                        dealsByEmployeeNumber[saljId] = existingCount + grouped.DealsCount;
                        continue;
                    }

                    dealsByEmployeeNumber[saljId] = grouped.DealsCount;
                }

                if (dealsByEmployeeNumber.Count == 0)
                {
                    continue;
                }

                var employeeNumbers = dealsByEmployeeNumber.Keys.ToList();
                var usersByUsername = await _userManager.Users
                    .AsNoTracking()
                    .Where(u => u.UserName != null && employeeNumbers.Contains(u.UserName))
                    .Select(u => new { u.Id, u.UserName })
                    .ToListAsync(cancellationToken);

                var userIdLookup = usersByUsername
                    .Where(u => u.UserName != null)
                    .ToDictionary(u => u.UserName!, u => u.Id, StringComparer.Ordinal);

                foreach (var row in dealsByEmployeeNumber)
                {
                    userIdLookup.TryGetValue(row.Key, out var userId);
                    _context.ContestEntries.Add(new ContestEntry
                    {
                        ContestId = contest.Id,
                        UserId = userId,
                        EmployeeNumber = row.Key,
                        DealsCount = row.Value,
                        UpdatedDate = DateTime.Now
                    });
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
