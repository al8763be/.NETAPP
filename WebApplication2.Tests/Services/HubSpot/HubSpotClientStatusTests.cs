using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebApplication2.Services.HubSpot;
using Xunit;

namespace WebApplication2.Tests.Services.HubSpot;

public class HubSpotClientStatusTests
{
    [Fact]
    public async Task GetFulfilledDealsAsync_TreatsConfiguredSwedishStatusesAsFulfilled()
    {
        var payload = """
        {
          "results": [
            {
              "id": "deal-1",
              "properties": {
                "dealname": "A",
                "dealstage": "klar kund",
                "closedate": "1739980800000",
                "hs_lastmodifieddate": "1739980800000",
                "email": "1111@stl.nu",
                "hubspot_owner_id": "owner-1",
                "amount": "100",
                "deal_currency_code": "SEK"
              }
            },
            {
              "id": "deal-2",
              "properties": {
                "dealname": "B",
                "dealstage": "Installerad - ej fakturerad,",
                "closedate": "1739980800000",
                "hs_lastmodifieddate": "1739980800000",
                "email": "2222@stl.nu",
                "hubspot_owner_id": "owner-2",
                "amount": "200",
                "deal_currency_code": "SEK"
              }
            }
          ]
        }
        """;

        var client = CreateClient(payload);

        var result = await client.GetFulfilledDealsAsync(null, null, 100);

        Assert.Equal(2, result.Deals.Count);
        Assert.All(result.Deals, d => Assert.True(d.IsFulfilled));
    }

    [Fact]
    public async Task GetFulfilledDealsAsync_MarksStatusesOutsideConfiguredListAsNotFulfilled()
    {
        var payload = """
        {
          "results": [
            {
              "id": "deal-3",
              "properties": {
                "dealname": "C",
                "dealstage": "förlorad",
                "closedate": "1739980800000",
                "hs_lastmodifieddate": "1739980800000",
                "email": "3333@stl.nu",
                "hubspot_owner_id": "owner-3",
                "amount": "300",
                "deal_currency_code": "SEK"
              }
            }
          ]
        }
        """;

        var client = CreateClient(payload);

        var result = await client.GetFulfilledDealsAsync(null, null, 100);

        var deal = Assert.Single(result.Deals);
        Assert.False(deal.IsFulfilled);
    }

    private static HubSpotClient CreateClient(string responsePayload)
    {
        var options = Options.Create(new HubSpotOptions
        {
            Enabled = true,
            AccessToken = "token",
            FulfilledProperty = "dealstage",
            FulfilledDateProperty = "closedate",
            LastModifiedProperty = "hs_lastmodifieddate",
            DealNameProperty = "dealname",
            OwnerEmailProperty = "email",
            OwnerIdProperty = "hubspot_owner_id",
            AmountProperty = "amount",
            CurrencyCodeProperty = "deal_currency_code",
            FulfilledValues =
            [
                "nyregistrerad",
                "ombokning",
                "bokad",
                "klar kund",
                "installerad - ej fakturerad"
            ]
        });

        var httpClient = new HttpClient(new StubHttpMessageHandler(responsePayload))
        {
            BaseAddress = new Uri("https://api.hubapi.com")
        };

        return new HubSpotClient(httpClient, options, NullLogger<HubSpotClient>.Instance);
    }

    private sealed class StubHttpMessageHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
