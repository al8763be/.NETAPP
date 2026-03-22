using WebApplication2.Services.HubSpot;

namespace WebApplication2.Tests.Helpers;

public sealed class FakeHubSpotClient : IHubSpotClient
{
    private readonly Queue<HubSpotDealsPageResult> _dealPages;
    private readonly Func<DateTime?, string?, int, HubSpotDealsPageResult>? _getFulfilledDeals;
    private readonly Func<DateTime, DateTime, string?, int, HubSpotDealsPageResult>? _searchDealsByClosedDateRange;
    private readonly Func<List<HubSpotDealRecord>, Task>? _enrichDealsWithLineItemsAsync;

    public FakeHubSpotClient(
        IEnumerable<HubSpotDealsPageResult>? dealPages = null,
        Func<DateTime?, string?, int, HubSpotDealsPageResult>? getFulfilledDeals = null,
        Func<DateTime, DateTime, string?, int, HubSpotDealsPageResult>? searchDealsByClosedDateRange = null,
        Func<List<HubSpotDealRecord>, Task>? enrichDealsWithLineItemsAsync = null)
    {
        _dealPages = new Queue<HubSpotDealsPageResult>(dealPages ?? Enumerable.Empty<HubSpotDealsPageResult>());
        _getFulfilledDeals = getFulfilledDeals;
        _searchDealsByClosedDateRange = searchDealsByClosedDateRange;
        _enrichDealsWithLineItemsAsync = enrichDealsWithLineItemsAsync;
    }

    public Task<HubSpotDealsPageResult> GetFulfilledDealsAsync(
        DateTime? modifiedSinceUtc,
        string? afterCursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (_getFulfilledDeals != null)
        {
            return Task.FromResult(_getFulfilledDeals(modifiedSinceUtc, afterCursor, pageSize));
        }

        if (_dealPages.Count == 0)
        {
            return Task.FromResult(new HubSpotDealsPageResult());
        }

        return Task.FromResult(_dealPages.Dequeue());
    }

    public Task<HubSpotDealsPageResult> SearchFulfilledDealsByClosedDateRangeAsync(
        DateTime closedDateStartUtc,
        DateTime closedDateEndUtc,
        string? afterCursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (_searchDealsByClosedDateRange != null)
        {
            return Task.FromResult(_searchDealsByClosedDateRange(closedDateStartUtc, closedDateEndUtc, afterCursor, pageSize));
        }

        if (_dealPages.Count == 0)
        {
            return Task.FromResult(new HubSpotDealsPageResult());
        }

        return Task.FromResult(_dealPages.Dequeue());
    }

    public Task EnrichDealsWithLineItemsAsync(
        List<HubSpotDealRecord> deals,
        CancellationToken cancellationToken = default)
    {
        if (_enrichDealsWithLineItemsAsync != null)
        {
            return _enrichDealsWithLineItemsAsync(deals);
        }

        return Task.CompletedTask;
    }
}
