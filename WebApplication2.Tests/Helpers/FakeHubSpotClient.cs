using WebApplication2.Services.HubSpot;

namespace WebApplication2.Tests.Helpers;

public sealed class FakeHubSpotClient : IHubSpotClient
{
    private readonly Queue<HubSpotDealsPageResult> _dealPages;
    private readonly Dictionary<string, HubSpotOwnerRecord?> _owners;

    public FakeHubSpotClient(
        IEnumerable<HubSpotDealsPageResult>? dealPages = null,
        IDictionary<string, HubSpotOwnerRecord?>? owners = null)
    {
        _dealPages = new Queue<HubSpotDealsPageResult>(dealPages ?? Enumerable.Empty<HubSpotDealsPageResult>());
        _owners = new Dictionary<string, HubSpotOwnerRecord?>(owners ?? new Dictionary<string, HubSpotOwnerRecord?>(), StringComparer.Ordinal);
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

    public Task<HubSpotOwnerRecord?> GetOwnerByOwnerIdAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        _owners.TryGetValue(ownerId, out var owner);
        return Task.FromResult(owner);
    }
}
