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
                ["properties"] = BuildPropertiesQuery()
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

            var filters = new List<object>
            {
                new
                {
                    propertyName = _options.FulfilledDateProperty,
                    @operator = "GTE",
                    value = ToUnixEpochMillisecondsString(closedDateStartUtc)
                },
                new
                {
                    propertyName = _options.FulfilledDateProperty,
                    @operator = "LTE",
                    value = ToUnixEpochMillisecondsString(closedDateEndUtc)
                },
                new
                {
                    propertyName = _options.FulfilledProperty,
                    @operator = "IN",
                    values = fulfilledStatuses.OrderBy(v => v).ToArray()
                },
                new
                {
                    propertyName = _options.OwnerIdProperty,
                    @operator = "HAS_PROPERTY"
                }
            };

            var requestBody = new Dictionary<string, object?>
            {
                ["filterGroups"] = new[]
                {
                    new
                    {
                        filters
                    }
                },
                ["properties"] = BuildPropertiesList(),
                ["limit"] = Math.Clamp(pageSize, 1, 100)
            };

            if (!string.IsNullOrWhiteSpace(afterCursor))
            {
                requestBody["after"] = afterCursor;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/crm/v3/objects/deals/search")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "HubSpot deal search failed for closed date range {StartUtc} - {EndUtc}. Status code {StatusCode}",
                    closedDateStartUtc,
                    closedDateEndUtc,
                    (int)response.StatusCode);

                return new HubSpotDealsPageResult();
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = ParseDealsPage(payload, null, fulfilledStatuses);
            _logger.LogInformation(
                "HubSpot deal search parsed. Deals: {Count}, NextCursor: {NextCursor}",
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
                _options.FulfilledDateProperty,
                _options.LastModifiedProperty,
                _options.AmountProperty,
                _options.CurrencyCodeProperty,
                _options.OwnerEmailProperty,
                _options.OwnerIdProperty
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
                if (!item.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                var externalDealId = idElement.GetString();
                if (string.IsNullOrWhiteSpace(externalDealId))
                {
                    continue;
                }

                if (!item.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var dealStage = ReadPropertyString(properties, _options.FulfilledProperty);
                var isFulfilled = IsFulfilledStatus(dealStage, fulfilledValues);

                var lastModifiedUtc = ParseHubSpotDate(ReadPropertyString(properties, _options.LastModifiedProperty));
                if (modifiedSinceUtc.HasValue && lastModifiedUtc.HasValue && lastModifiedUtc.Value < modifiedSinceUtc.Value)
                {
                    continue;
                }

                var fulfilledDateUtc = ParseHubSpotDate(ReadPropertyString(properties, _options.FulfilledDateProperty));

                var ownerEmail = ReadPropertyString(properties, _options.OwnerEmailProperty);
                var ownerId = ReadPropertyString(properties, _options.OwnerIdProperty);
                var amount = ParseNullableDecimal(ReadPropertyString(properties, _options.AmountProperty));
                var currency = ReadPropertyString(properties, _options.CurrencyCodeProperty);
                var dealName = ReadPropertyString(properties, _options.DealNameProperty);

                var rowPayload = item.GetRawText();
                var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rowPayload)));

                result.Deals.Add(new HubSpotDealRecord
                {
                    ExternalDealId = externalDealId,
                    DealName = dealName,
                    OwnerEmail = ownerEmail,
                    OwnerId = ownerId,
                    IsFulfilled = isFulfilled,
                    FulfilledDateUtc = fulfilledDateUtc,
                    LastModifiedUtc = lastModifiedUtc,
                    Amount = amount,
                    CurrencyCode = currency,
                    DealStage = dealStage,
                    PayloadHash = payloadHash
                });
            }

            return result;
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

            if (!properties.TryGetProperty(propertyName, out var value))
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

            if (DateTimeOffset.TryParse(value, out var parsedDate))
            {
                return parsedDate.UtcDateTime;
            }

            return null;
        }

        private static string ToUnixEpochMillisecondsString(DateTime value)
        {
            var utcValue = value.Kind == DateTimeKind.Utc
                ? value
                : value.ToUniversalTime();

            return DateTimeOffset
                .FromUnixTimeMilliseconds(new DateTimeOffset(utcValue).ToUnixTimeMilliseconds())
                .ToUnixTimeMilliseconds()
                .ToString(CultureInfo.InvariantCulture);
        }
    }
}
