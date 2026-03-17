using WebApplication2.Services.HubSpot;

namespace WebApplication2.Tests.Helpers;

public sealed class FakeHubSpotClient : IHubSpotClient
{
    private readonly Queue<HubSpotDealsPageResult> _dealPages;

    public FakeHubSpotClient(
        IEnumerable<HubSpotDealsPageResult>? dealPages = null)
    {
        _dealPages = new Queue<HubSpotDealsPageResult>(dealPages ?? Enumerable.Empty<HubSpotDealsPageResult>());
    }

    public Task<HubSpotDealsPageResult> GetFulfilledDealsAsync(
        DateTime? modifiedSinceUtc,
        string? afterCursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
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
        if (_dealPages.Count == 0)
        {
            return Task.FromResult(new HubSpotDealsPageResult());
        }

        return Task.FromResult(_dealPages.Dequeue());
    }
}
