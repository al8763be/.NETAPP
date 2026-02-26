using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace WebApplication2.Services.HubSpot
{
    public class HubSpotClient : IHubSpotClient
    {
        private readonly HttpClient _httpClient;
        private readonly HubSpotOptions _options;
        private readonly ILogger<HubSpotClient> _logger;
        private readonly Dictionary<string, HubSpotOwnerRecord?> _ownerCache = new(StringComparer.Ordinal);
        private HashSet<string>? _resolvedFulfilledStatuses;

        private sealed class ContactFieldValues
        {
            public string? Saljare { get; set; }
            public DateTime? SaleDateUtc { get; set; }
        }

        private sealed class ContactSearchRow
        {
            public string ContactId { get; set; } = string.Empty;
            public string? Saljare { get; set; }
            public DateTime? SaleDateUtc { get; set; }
        }

        private sealed class ContactSearchPageResult
        {
            public List<ContactSearchRow> Contacts { get; set; } = new();
            public string? NextCursor { get; set; }
        }

        public HubSpotClient(HttpClient httpClient, IOptions<HubSpotOptions> options, ILogger<HubSpotClient> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<HubSpotDealsPageResult> GetFulfilledDealsAsync(
            DateTime? modifiedSinceUtc,
            string? afterCursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return new HubSpotDealsPageResult();
            }

            if (string.IsNullOrWhiteSpace(_options.AccessToken))
            {
                throw new InvalidOperationException("HubSpot access token is missing.");
            }

            var query = new Dictionary<string, string?>
            {
                ["limit"] = Math.Clamp(pageSize, 1, 100).ToString(),
                ["archived"] = "false",
                ["properties"] = BuildPropertiesQuery(),
                ["associations"] = "contacts"
            };

            if (!string.IsNullOrWhiteSpace(afterCursor))
            {
                query["after"] = afterCursor;
            }

            if (modifiedSinceUtc.HasValue)
            {
                query["updatedAtGte"] = modifiedSinceUtc.Value.ToString("O");
            }

            var uri = QueryHelpers.AddQueryString("/crm/v3/objects/deals", query!);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("HubSpot deals fetch failed with status code {StatusCode}", (int)response.StatusCode);
                return new HubSpotDealsPageResult();
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var fulfilledStatuses = await GetResolvedFulfilledStatusesAsync(cancellationToken);
            var result = ParseDealsPage(payload, modifiedSinceUtc, fulfilledStatuses);
            await EnrichDealsWithContactDataAsync(result.Deals, cancellationToken);
            _logger.LogInformation(
                "HubSpot deals page parsed. Deals: {Count}, NextCursor: {NextCursor}",
                result.Deals.Count,
                result.NextCursor ?? "<none>");
            return result;
        }

        public async Task<HubSpotDealsPageResult> SearchFulfilledDealsByClosedDateRangeAsync(
            DateTime closedDateStartUtc,
            DateTime closedDateEndUtc,
            string? afterCursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return new HubSpotDealsPageResult();
            }

            if (string.IsNullOrWhiteSpace(_options.AccessToken))
            {
                throw new InvalidOperationException("HubSpot access token is missing.");
            }

            var fulfilledStatuses = await GetResolvedFulfilledStatusesAsync(cancellationToken);
            if (fulfilledStatuses.Count == 0)
            {
                return new HubSpotDealsPageResult();
            }

            var contactPage = await SearchContactsBySaleDateRangeAsync(
                closedDateStartUtc,
                closedDateEndUtc,
                afterCursor,
                Math.Clamp(pageSize, 1, 100),
                cancellationToken);

            if (contactPage.Contacts.Count == 0)
            {
                return new HubSpotDealsPageResult
                {
                    NextCursor = contactPage.NextCursor
                };
            }

            var contactIds = contactPage.Contacts.Select(c => c.ContactId).ToList();
            var contactRowsById = contactPage.Contacts.ToDictionary(c => c.ContactId, c => c, StringComparer.Ordinal);
            var dealIdsByContactId = await GetAssociatedDealIdsForContactsAsync(contactIds, cancellationToken);

            var dealToContactIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kvp in dealIdsByContactId)
            {
                foreach (var dealId in kvp.Value)
                {
                    if (!dealToContactIds.TryGetValue(dealId, out var contactList))
                    {
                        contactList = new List<string>();
                        dealToContactIds[dealId] = contactList;
                    }

                    if (!contactList.Contains(kvp.Key, StringComparer.Ordinal))
                    {
                        contactList.Add(kvp.Key);
                    }
                }
            }

            var uniqueDealIds = dealToContactIds.Keys.ToList();
            var deals = await GetDealsByIdsAsync(uniqueDealIds, fulfilledStatuses, cancellationToken);

            foreach (var deal in deals)
            {
                if (!dealToContactIds.TryGetValue(deal.ExternalDealId, out var relatedContactIds))
                {
                    continue;
                }

                deal.ContactIds = relatedContactIds;
                foreach (var contactId in relatedContactIds)
                {
                    if (!contactRowsById.TryGetValue(contactId, out var contact))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(contact.Saljare))
                    {
                        deal.SaljId = contact.Saljare.Trim();
                    }

                    if (contact.SaleDateUtc.HasValue)
                    {
                        deal.FulfilledDateUtc = contact.SaleDateUtc.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(deal.SaljId) && deal.FulfilledDateUtc.HasValue)
                    {
                        break;
                    }
                }
            }

            var result = new HubSpotDealsPageResult
            {
                Deals = deals
                    .Where(d =>
                        d.IsFulfilled &&
                        d.FulfilledDateUtc.HasValue &&
                        d.FulfilledDateUtc.Value >= closedDateStartUtc &&
                        d.FulfilledDateUtc.Value <= closedDateEndUtc)
                    .ToList(),
                NextCursor = contactPage.NextCursor
            };

            _logger.LogInformation(
                "HubSpot contact-window search parsed. Contacts: {ContactCount}, Deals: {Count}, NextCursor: {NextCursor}",
                contactPage.Contacts.Count,
                result.Deals.Count,
                result.NextCursor ?? "<none>");

            return result;
        }

        public async Task<HubSpotOwnerRecord?> GetOwnerByOwnerIdAsync(
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
            {
                return null;
            }

            if (_ownerCache.TryGetValue(ownerId, out var cachedOwner))
            {
                return cachedOwner;
            }

            if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.AccessToken))
            {
                return null;
            }

            string? payload = null;
            using (var response = await SendOwnerLookupRequestAsync(ownerId, includeArchived: false, cancellationToken))
            {
                if (response.IsSuccessStatusCode)
                {
                    payload = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                else if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    _logger.LogWarning(
                        "HubSpot owner lookup failed for owner id {OwnerId} with status code {StatusCode}",
                        ownerId,
                        (int)response.StatusCode);

                    _ownerCache[ownerId] = null;
                    return null;
                }
            }

            // HubSpot owner records can become archived while still referenced by deals.
            // Retry with archived=true before concluding the owner is missing.
            if (payload == null)
            {
                using var archivedResponse = await SendOwnerLookupRequestAsync(ownerId, includeArchived: true, cancellationToken);
                if (!archivedResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "HubSpot owner lookup failed for owner id {OwnerId} with archived fallback. Status code {StatusCode}",
                        ownerId,
                        (int)archivedResponse.StatusCode);

                    _ownerCache[ownerId] = null;
                    return null;
                }

                payload = await archivedResponse.Content.ReadAsStringAsync(cancellationToken);
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var owner = new HubSpotOwnerRecord
            {
                OwnerId = ownerId,
                Email = root.TryGetProperty("email", out var emailElement) && emailElement.ValueKind == JsonValueKind.String
                    ? emailElement.GetString()
                    : null,
                FirstName = root.TryGetProperty("firstName", out var firstNameElement) && firstNameElement.ValueKind == JsonValueKind.String
                    ? firstNameElement.GetString()
                    : null,
                LastName = root.TryGetProperty("lastName", out var lastNameElement) && lastNameElement.ValueKind == JsonValueKind.String
                    ? lastNameElement.GetString()
                    : null,
                IsArchived = root.TryGetProperty("archived", out var archivedElement) &&
                             archivedElement.ValueKind == JsonValueKind.True
            };

            if (root.TryGetProperty("teams", out var teamsElement) && teamsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var team in teamsElement.EnumerateArray())
                {
                    if (!team.TryGetProperty("name", out var teamNameElement) || teamNameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var teamName = teamNameElement.GetString();
                    if (string.IsNullOrWhiteSpace(teamName))
                    {
                        continue;
                    }

                    owner.TeamNames.Add(teamName);

                    if (string.IsNullOrWhiteSpace(owner.PrimaryTeamName) &&
                        team.TryGetProperty("primary", out var primaryElement) &&
                        primaryElement.ValueKind == JsonValueKind.True)
                    {
                        owner.PrimaryTeamName = teamName;
                    }
                }

                if (string.IsNullOrWhiteSpace(owner.PrimaryTeamName) && owner.TeamNames.Count > 0)
                {
                    owner.PrimaryTeamName = owner.TeamNames[0];
                }
            }

            _ownerCache[ownerId] = owner;
            return owner;
        }

        private async Task<HttpResponseMessage> SendOwnerLookupRequestAsync(
            string ownerId,
            bool includeArchived,
            CancellationToken cancellationToken)
        {
            var path = includeArchived
                ? $"/crm/v3/owners/{Uri.EscapeDataString(ownerId)}?archived=true"
                : $"/crm/v3/owners/{Uri.EscapeDataString(ownerId)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
            return await _httpClient.SendAsync(request, cancellationToken);
        }

        private string BuildPropertiesQuery()
            => string.Join(",", BuildPropertiesList());

        private List<string> BuildPropertiesList()
        {
            return new[]
            {
                _options.DealNameProperty,
                _options.FulfilledProperty,
                _options.DealFallbackDateProperty,
                _options.LastModifiedProperty,
                _options.AmountProperty,
                _options.CurrencyCodeProperty,
                _options.ProvisionProperty,
                _options.OwnerEmailProperty,
                _options.OwnerIdProperty,
                _options.SaljIdProperty
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        }

        private HubSpotDealsPageResult ParseDealsPage(
            string payload,
            DateTime? modifiedSinceUtc,
            HashSet<string> fulfilledValues)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var result = new HubSpotDealsPageResult();

            if (root.TryGetProperty("paging", out var paging) &&
                paging.TryGetProperty("next", out var next) &&
                next.TryGetProperty("after", out var after))
            {
                result.NextCursor = after.GetString();
            }

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in results.EnumerateArray())
            {
                var deal = ParseDealRecord(item, modifiedSinceUtc, fulfilledValues);
                if (deal != null)
                {
                    result.Deals.Add(deal);
                }
            }

            return result;
        }

        private HubSpotDealRecord? ParseDealRecord(
            JsonElement item,
            DateTime? modifiedSinceUtc,
            HashSet<string> fulfilledValues)
        {
            if (!item.TryGetProperty("id", out var idElement))
            {
                return null;
            }

            var externalDealId = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.GetRawText(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(externalDealId))
            {
                return null;
            }

            if (!item.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var dealStage = ReadPropertyString(properties, _options.FulfilledProperty);
            var isFulfilled = IsFulfilledStatus(dealStage, fulfilledValues);

            var lastModifiedUtc = ParseHubSpotDate(ReadPropertyString(properties, _options.LastModifiedProperty));
            if (modifiedSinceUtc.HasValue && lastModifiedUtc.HasValue && lastModifiedUtc.Value < modifiedSinceUtc.Value)
            {
                return null;
            }

            var fulfilledDateUtc = ParseHubSpotDate(ReadPropertyString(properties, _options.DealFallbackDateProperty));

            var ownerEmail = ReadPropertyString(properties, _options.OwnerEmailProperty);
            var ownerId = ReadPropertyString(properties, _options.OwnerIdProperty);
            var saljId = ReadPropertyString(properties, _options.SaljIdProperty);
            var amount = ParseNullableDecimal(ReadPropertyString(properties, _options.AmountProperty));
            var sellerProvision = ParseNullableDecimal(ReadPropertyString(properties, _options.ProvisionProperty));
            var currency = ReadPropertyString(properties, _options.CurrencyCodeProperty);
            var dealName = ReadPropertyString(properties, _options.DealNameProperty);

            var rowPayload = item.GetRawText();
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rowPayload)));

            return new HubSpotDealRecord
            {
                ExternalDealId = externalDealId,
                DealName = dealName,
                OwnerEmail = ownerEmail,
                OwnerId = ownerId,
                SaljId = saljId,
                ContactIds = ReadAssociatedContactIds(item),
                IsFulfilled = isFulfilled,
                FulfilledDateUtc = fulfilledDateUtc,
                LastModifiedUtc = lastModifiedUtc,
                Amount = amount,
                SellerProvision = sellerProvision,
                CurrencyCode = currency,
                DealStage = dealStage,
                PayloadHash = payloadHash
            };
        }

        private async Task EnrichDealsWithContactDataAsync(
            List<HubSpotDealRecord> deals,
            CancellationToken cancellationToken)
        {
            if (deals.Count == 0 || string.IsNullOrWhiteSpace(_options.AccessToken))
            {
                return;
            }

            var contactIdsByDealId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var deal in deals)
            {
                if (string.IsNullOrWhiteSpace(deal.ExternalDealId))
                {
                    continue;
                }

                var contactIds = deal.ContactIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (contactIds.Count == 0)
                {
                    contactIds = await GetAssociatedContactIdsForDealAsync(deal.ExternalDealId, cancellationToken);
                }

                if (contactIds.Count == 0)
                {
                    continue;
                }

                contactIdsByDealId[deal.ExternalDealId] = contactIds;
                deal.ContactIds = contactIds;
            }

            if (contactIdsByDealId.Count == 0)
            {
                return;
            }

            var uniqueContactIds = contactIdsByDealId.Values
                .SelectMany(ids => ids)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var contactsById = await GetContactValuesByIdsAsync(uniqueContactIds, cancellationToken);
            if (contactsById.Count == 0)
            {
                return;
            }

            foreach (var deal in deals)
            {
                if (!contactIdsByDealId.TryGetValue(deal.ExternalDealId, out var contactIds))
                {
                    continue;
                }

                foreach (var contactId in contactIds)
                {
                    if (!contactsById.TryGetValue(contactId, out var contactValues))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(contactValues.Saljare))
                    {
                        deal.SaljId = contactValues.Saljare.Trim();
                    }

                    if (contactValues.SaleDateUtc.HasValue)
                    {
                        deal.FulfilledDateUtc = contactValues.SaleDateUtc.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(deal.SaljId) && deal.FulfilledDateUtc.HasValue)
                    {
                        break;
                    }
                }
            }
        }

        private async Task<List<string>> GetAssociatedContactIdsForDealAsync(
            string dealId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(dealId))
            {
                return new List<string>();
            }

            var path = $"/crm/v3/objects/deals/{Uri.EscapeDataString(dealId)}/associations/contacts?limit=50";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "HubSpot contact association lookup failed for deal {DealId} with status code {StatusCode}",
                    dealId,
                    (int)response.StatusCode);
                return new List<string>();
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            return results
                .EnumerateArray()
                .Select(result => result.TryGetProperty("id", out var idElement) ? idElement.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private async Task<Dictionary<string, ContactFieldValues>> GetContactValuesByIdsAsync(
            IReadOnlyCollection<string> contactIds,
            CancellationToken cancellationToken)
        {
            if (contactIds.Count == 0)
            {
                return new Dictionary<string, ContactFieldValues>(StringComparer.Ordinal);
            }

            var requestBody = new Dictionary<string, object?>
            {
                ["properties"] = new[]
                {
                    _options.ContactSaljareProperty,
                    _options.FulfilledDateProperty
                }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
                ["inputs"] = contactIds.Select(id => new Dictionary<string, string> { ["id"] = id }).ToArray()
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "/crm/v3/objects/contacts/batch/read")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "HubSpot contact batch read failed with status code {StatusCode}",
                    (int)response.StatusCode);
                return new Dictionary<string, ContactFieldValues>(StringComparer.Ordinal);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var result = new Dictionary<string, ContactFieldValues>(StringComparer.Ordinal);

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var row in results.EnumerateArray())
            {
                if (!row.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var contactId = idElement.GetString();
                if (string.IsNullOrWhiteSpace(contactId))
                {
                    continue;
                }

                if (!row.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var saljare = ReadPropertyString(properties, _options.ContactSaljareProperty);
                var saleDateRaw = ReadPropertyString(properties, _options.FulfilledDateProperty);
                var saleDate = ParseHubSpotDate(saleDateRaw);

                result[contactId] = new ContactFieldValues
                {
                    Saljare = saljare,
                    SaleDateUtc = saleDate
                };
            }

            return result;
        }

        private async Task<ContactSearchPageResult> SearchContactsBySaleDateRangeAsync(
            DateTime saleDateStartUtc,
            DateTime saleDateEndUtc,
            string? afterCursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var startMs = new DateTimeOffset(saleDateStartUtc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var endMs = new DateTimeOffset(saleDateEndUtc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

            var requestBody = new Dictionary<string, object?>
            {
                ["filterGroups"] = new[]
                {
                    new
                    {
                        filters = new object[]
                        {
                            new { propertyName = _options.FulfilledDateProperty, @operator = "GTE", value = startMs },
                            new { propertyName = _options.FulfilledDateProperty, @operator = "LTE", value = endMs }
                        }
                    }
                },
                ["properties"] = new[]
                {
                    _options.ContactSaljareProperty,
                    _options.FulfilledDateProperty
                }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
                ["limit"] = Math.Clamp(pageSize, 1, 100)
            };

            if (!string.IsNullOrWhiteSpace(afterCursor))
            {
                requestBody["after"] = afterCursor;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/crm/v3/objects/contacts/search")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "HubSpot contact search failed for sale date range {StartUtc} - {EndUtc}. Status code {StatusCode}",
                    saleDateStartUtc,
                    saleDateEndUtc,
                    (int)response.StatusCode);

                return new ContactSearchPageResult();
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var result = new ContactSearchPageResult();
            if (root.TryGetProperty("paging", out var paging) &&
                paging.TryGetProperty("next", out var next) &&
                next.TryGetProperty("after", out var after))
            {
                result.NextCursor = after.GetString();
            }

            if (!root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var contactId = idElement.GetString();
                if (string.IsNullOrWhiteSpace(contactId))
                {
                    continue;
                }

                if (!row.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var saljare = ReadPropertyString(properties, _options.ContactSaljareProperty);
                var saleDate = ParseHubSpotDate(ReadPropertyString(properties, _options.FulfilledDateProperty));

                result.Contacts.Add(new ContactSearchRow
                {
                    ContactId = contactId,
                    Saljare = saljare,
                    SaleDateUtc = saleDate
                });
            }

            return result;
        }

        private async Task<Dictionary<string, List<string>>> GetAssociatedDealIdsForContactsAsync(
            IReadOnlyCollection<string> contactIds,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (contactIds.Count == 0)
            {
                return result;
            }

            const int chunkSize = 100;
            var allIds = contactIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            for (var index = 0; index < allIds.Count; index += chunkSize)
            {
                var chunk = allIds.Skip(index).Take(chunkSize).ToList();
                var requestBody = new Dictionary<string, object?>
                {
                    ["inputs"] = chunk.Select(id => new Dictionary<string, string> { ["id"] = id }).ToArray()
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "/crm/v4/associations/contacts/deals/batch/read")
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "HubSpot contact->deal association batch read failed with status code {StatusCode}",
                        (int)response.StatusCode);
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (!root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("from", out var fromElement) ||
                        fromElement.ValueKind != JsonValueKind.Object ||
                        !fromElement.TryGetProperty("id", out var fromIdElement) ||
                        fromIdElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var contactId = fromIdElement.GetString();
                    if (string.IsNullOrWhiteSpace(contactId))
                    {
                        continue;
                    }

                    if (!row.TryGetProperty("to", out var toElement) || toElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(contactId, out var dealIds))
                    {
                        dealIds = new List<string>();
                        result[contactId] = dealIds;
                    }

                    foreach (var association in toElement.EnumerateArray())
                    {
                        if (!association.TryGetProperty("toObjectId", out var dealIdElement))
                        {
                            continue;
                        }

                        var dealId = dealIdElement.ValueKind switch
                        {
                            JsonValueKind.String => dealIdElement.GetString(),
                            JsonValueKind.Number => dealIdElement.GetRawText(),
                            _ => null
                        };

                        if (string.IsNullOrWhiteSpace(dealId))
                        {
                            continue;
                        }

                        if (!dealIds.Contains(dealId, StringComparer.Ordinal))
                        {
                            dealIds.Add(dealId);
                        }
                    }
                }
            }

            return result;
        }

        private async Task<List<HubSpotDealRecord>> GetDealsByIdsAsync(
            IReadOnlyCollection<string> dealIds,
            HashSet<string> fulfilledStatuses,
            CancellationToken cancellationToken)
        {
            var result = new List<HubSpotDealRecord>();
            if (dealIds.Count == 0)
            {
                return result;
            }

            const int chunkSize = 100;
            var allIds = dealIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            for (var index = 0; index < allIds.Count; index += chunkSize)
            {
                var chunk = allIds.Skip(index).Take(chunkSize).ToList();
                var requestBody = new Dictionary<string, object?>
                {
                    ["properties"] = BuildPropertiesList(),
                    ["inputs"] = chunk.Select(id => new Dictionary<string, string> { ["id"] = id }).ToArray()
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "/crm/v3/objects/deals/batch/read")
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "HubSpot deal batch read failed with status code {StatusCode}",
                        (int)response.StatusCode);
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;

                if (!root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var row in rows.EnumerateArray())
                {
                    var deal = ParseDealRecord(row, modifiedSinceUtc: null, fulfilledStatuses);
                    if (deal != null)
                    {
                        result.Add(deal);
                    }
                }
            }

            return result;
        }

        private static List<string> ReadAssociatedContactIds(JsonElement item)
        {
            if (!item.TryGetProperty("associations", out var associationsElement) ||
                associationsElement.ValueKind != JsonValueKind.Object)
            {
                return new List<string>();
            }

            if (!associationsElement.TryGetProperty("contacts", out var contactsElement) ||
                contactsElement.ValueKind != JsonValueKind.Object)
            {
                return new List<string>();
            }

            if (!contactsElement.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            return resultsElement
                .EnumerateArray()
                .Select(row => row.TryGetProperty("id", out var idElement) ? idElement.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private async Task<HashSet<string>> GetResolvedFulfilledStatusesAsync(CancellationToken cancellationToken)
        {
            if (_resolvedFulfilledStatuses != null)
            {
                return _resolvedFulfilledStatuses;
            }

            var configuredValues = _options.FulfilledValues?
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(NormalizeStatusValue)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList() ?? new List<string>();

            if (!string.IsNullOrWhiteSpace(_options.FulfilledValue))
            {
                var normalizedFallback = NormalizeStatusValue(_options.FulfilledValue);
                if (!string.IsNullOrWhiteSpace(normalizedFallback))
                {
                    configuredValues.Add(normalizedFallback);
                }
            }

            var configuredSet = configuredValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var resolvedValues = configuredSet.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (resolvedValues.Count == 0 ||
                string.IsNullOrWhiteSpace(_options.AccessToken) ||
                string.IsNullOrWhiteSpace(_options.FulfilledProperty))
            {
                _resolvedFulfilledStatuses = resolvedValues;
                return _resolvedFulfilledStatuses;
            }

            try
            {
                // Try to resolve option values from property metadata when available.
                var propertyPath = $"/crm/v3/properties/deals/{Uri.EscapeDataString(_options.FulfilledProperty)}";
                using var propertyRequest = new HttpRequestMessage(HttpMethod.Get, propertyPath);
                propertyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

                using var propertyResponse = await _httpClient.SendAsync(propertyRequest, cancellationToken);
                if (!propertyResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "HubSpot property lookup failed for {PropertyName} with status code {StatusCode}",
                        _options.FulfilledProperty,
                        (int)propertyResponse.StatusCode);
                }
                else
                {
                    var payload = await propertyResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var document = JsonDocument.Parse(payload);
                    var root = document.RootElement;

                    if (root.TryGetProperty("options", out var optionsElement) &&
                        optionsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var option in optionsElement.EnumerateArray())
                        {
                            var normalizedLabel = option.TryGetProperty("label", out var labelElement) &&
                                                  labelElement.ValueKind == JsonValueKind.String
                                ? NormalizeStatusValue(labelElement.GetString())
                                : string.Empty;

                            var normalizedValue = option.TryGetProperty("value", out var valueElement) &&
                                                  valueElement.ValueKind == JsonValueKind.String
                                ? NormalizeStatusValue(valueElement.GetString())
                                : string.Empty;

                            if (IsConfiguredStatusMatch(configuredSet, normalizedLabel) ||
                                IsConfiguredStatusMatch(configuredSet, normalizedValue))
                            {
                                if (!string.IsNullOrWhiteSpace(normalizedLabel))
                                {
                                    resolvedValues.Add(normalizedLabel);
                                }

                                if (!string.IsNullOrWhiteSpace(normalizedValue))
                                {
                                    resolvedValues.Add(normalizedValue);
                                }
                            }
                        }
                    }
                }

                // Fallback: deal stage values are often pipeline stage IDs, not property option values.
                using var pipelineRequest = new HttpRequestMessage(HttpMethod.Get, "/crm/v3/pipelines/deals");
                pipelineRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

                using var pipelineResponse = await _httpClient.SendAsync(pipelineRequest, cancellationToken);
                if (!pipelineResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "HubSpot pipeline lookup failed with status code {StatusCode}",
                        (int)pipelineResponse.StatusCode);
                }
                else
                {
                    var pipelinePayload = await pipelineResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var pipelineDocument = JsonDocument.Parse(pipelinePayload);
                    var pipelineRoot = pipelineDocument.RootElement;

                    if (pipelineRoot.TryGetProperty("results", out var pipelinesElement) &&
                        pipelinesElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var pipeline in pipelinesElement.EnumerateArray())
                        {
                            if (!pipeline.TryGetProperty("stages", out var stagesElement) ||
                                stagesElement.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            foreach (var stage in stagesElement.EnumerateArray())
                            {
                                var normalizedStageId = stage.TryGetProperty("id", out var idElement) &&
                                                        idElement.ValueKind == JsonValueKind.String
                                    ? NormalizeStatusValue(idElement.GetString())
                                    : string.Empty;

                                var normalizedStageLabel = stage.TryGetProperty("label", out var labelElement) &&
                                                           labelElement.ValueKind == JsonValueKind.String
                                    ? NormalizeStatusValue(labelElement.GetString())
                                    : string.Empty;

                                if (IsConfiguredStatusMatch(configuredSet, normalizedStageId) ||
                                    IsConfiguredStatusMatch(configuredSet, normalizedStageLabel))
                                {
                                    if (!string.IsNullOrWhiteSpace(normalizedStageId))
                                    {
                                        resolvedValues.Add(normalizedStageId);
                                    }

                                    if (!string.IsNullOrWhiteSpace(normalizedStageLabel))
                                    {
                                        resolvedValues.Add(normalizedStageLabel);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve HubSpot fulfilled option values");
            }

            _resolvedFulfilledStatuses = resolvedValues;
            return _resolvedFulfilledStatuses;
        }

        private static bool IsFulfilledStatus(string? status, HashSet<string> fulfilledValues)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            var normalizedStatus = NormalizeStatusValue(status);
            return !string.IsNullOrWhiteSpace(normalizedStatus) && fulfilledValues.Contains(normalizedStatus);
        }

        private static bool IsConfiguredStatusMatch(HashSet<string> configuredValues, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            if (configuredValues.Contains(candidate))
            {
                return true;
            }

            foreach (var configured in configuredValues)
            {
                if (string.IsNullOrWhiteSpace(configured))
                {
                    continue;
                }

                if (configured.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(configured, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeStatusValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            // HubSpot option labels can vary slightly in punctuation/spacing.
            var normalized = value
                .Trim()
                .Normalize(NormalizationForm.FormKC)
                .Replace('\u2013', '-')
                .Replace('\u2014', '-');

            normalized = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            normalized = normalized.TrimEnd('.', ',', ';', ':');

            return normalized;
        }

        private static string? ReadPropertyString(JsonElement properties, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            if (!TryGetPropertyCaseAndAccentInsensitive(properties, propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static bool TryGetPropertyCaseAndAccentInsensitive(
            JsonElement properties,
            string propertyName,
            out JsonElement value)
        {
            if (properties.TryGetProperty(propertyName, out value))
            {
                return true;
            }

            var normalizedTarget = NormalizePropertyLookupKey(propertyName);
            foreach (var candidate in properties.EnumerateObject())
            {
                if (candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) ||
                    NormalizePropertyLookupKey(candidate.Name).Equals(normalizedTarget, StringComparison.Ordinal))
                {
                    value = candidate.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string NormalizePropertyLookupKey(string value)
        {
            var normalized = value
                .Trim()
                .Normalize(NormalizationForm.FormD)
                .ToLowerInvariant();

            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static decimal? ParseNullableDecimal(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static DateTime? ParseHubSpotDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (long.TryParse(value, out var epochMilliseconds))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds).UtcDateTime;
                }
                catch
                {
                    return null;
                }
            }

            if (DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDateOnly))
            {
                return DateTime.SpecifyKind(parsedDateOnly, DateTimeKind.Utc);
            }

            if (DateTimeOffset.TryParse(value, out var parsedDate))
            {
                return parsedDate.UtcDateTime;
            }

            return null;
        }
    }
}
