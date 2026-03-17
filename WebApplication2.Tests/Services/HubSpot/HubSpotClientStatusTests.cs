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
    public async Task GetFulfilledDealsAsync_TreatsConfiguredSwedishStatusesAsFulfilled_UsingContactFields()
    {
        var dealsPayload = """
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
                "deal_currency_code": "SEK",
                "saljarprovision": "12.50"
              },
              "associations": {
                "contacts": {
                  "results": [
                    { "id": "contact-1", "type": "deal_to_contact" }
                  ]
                }
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
                "deal_currency_code": "SEK",
                "saljarprovision": null
              },
              "associations": {
                "contacts": {
                  "results": [
                    { "id": "contact-2", "type": "deal_to_contact" }
                  ]
                }
              }
            }
          ]
        }
        """;

        var contactsPayload = """
        {
          "results": [
            {
              "id": "contact-1",
              "properties": {
                "saljare": "1111",
                "forsaljningsdatum": "2025-02-20"
              }
            },
            {
              "id": "contact-2",
              "properties": {
                "saljare": "2222",
                "forsaljningsdatum": "2025-02-21"
              }
            }
          ]
        }
        """;

        var dealToContactAssociationsPayload = """
        {
          "results": [
            {
              "from": { "id": "deal-1" },
              "to": [
                { "toObjectId": "contact-1" }
              ]
            },
            {
              "from": { "id": "deal-2" },
              "to": [
                { "toObjectId": "contact-2" }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(
            dealsPayload,
            contactsPayload,
            dealToContactAssociationsPayload: dealToContactAssociationsPayload);

        var result = await client.GetFulfilledDealsAsync(null, null, 100);

        Assert.Equal(2, result.Deals.Count);
        Assert.All(result.Deals, d => Assert.True(d.IsFulfilled));
        Assert.Equal("1111", result.Deals[0].SaljId);
        Assert.Equal(new DateTime(2025, 2, 20, 0, 0, 0, DateTimeKind.Utc), result.Deals[0].FulfilledDateUtc);
        Assert.Equal(12.5m, result.Deals[0].SellerProvision);
        Assert.Null(result.Deals[1].SellerProvision);
    }

    [Fact]
    public async Task GetFulfilledDealsAsync_MarksStatusesOutsideConfiguredListAsNotFulfilled()
    {
        var dealsPayload = """
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
                "deal_currency_code": "SEK",
                "saljarprovision": "30.00"
              }
            }
          ]
        }
        """;

        var client = CreateClient(dealsPayload, """{"results":[]}""");

        var result = await client.GetFulfilledDealsAsync(null, null, 100);

        var deal = Assert.Single(result.Deals);
        Assert.False(deal.IsFulfilled);
    }

    [Fact]
    public async Task GetFulfilledDealsAsync_UsesPrimaryAssociatedContact_WhenMultipleContactsExist()
    {
        var dealsPayload = """
        {
          "results": [
            {
              "id": "deal-primary",
              "properties": {
                "dealname": "Primary-linked deal",
                "dealstage": "klar kund",
                "closedate": "1739980800000",
                "hs_lastmodifieddate": "1739980800000",
                "amount": "100",
                "deal_currency_code": "SEK"
              }
            }
          ]
        }
        """;

        var contactsPayload = """
        {
          "results": [
            {
              "id": "contact-secondary",
              "properties": {
                "saljare": "1111",
                "forsaljningsdatum": "2025-02-20",
                "kundstatus": "Klar kund"
              }
            },
            {
              "id": "contact-primary",
              "properties": {
                "saljare": "2222",
                "forsaljningsdatum": "2025-02-21",
                "kundstatus": "Klar kund"
              }
            }
          ]
        }
        """;

        var dealToContactAssociationsPayload = """
        {
          "results": [
            {
              "from": { "id": "deal-primary" },
              "to": [
                {
                  "toObjectId": "contact-secondary",
                  "associationTypes": [
                    { "category": "HUBSPOT_DEFINED", "typeId": 4, "label": null }
                  ]
                },
                {
                  "toObjectId": "contact-primary",
                  "associationTypes": [
                    { "category": "USER_DEFINED", "typeId": 9001, "label": "Primary Contact" }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(
            dealsPayload,
            contactsPayload,
            dealToContactAssociationsPayload: dealToContactAssociationsPayload);

        var result = await client.GetFulfilledDealsAsync(null, null, 100);

        var deal = Assert.Single(result.Deals);
        Assert.Equal("2222", deal.SaljId);
        Assert.Equal(new DateTime(2025, 2, 21, 0, 0, 0, DateTimeKind.Utc), deal.FulfilledDateUtc);
        Assert.Equal("Klar kund", deal.ContactKundstatus);
        Assert.Equal(new[] { "contact-primary" }, deal.ContactIds);
    }

    [Theory]
    [InlineData("Avslag")]
    [InlineData("Annullerat")]
    [InlineData("Winback")]
    [InlineData("Säljare")]
    public async Task GetFulfilledDealsAsync_MarksDealAsNotFulfilled_WhenSelectedContactKundstatusExcludesFulfilledStatus(string kundstatus)
    {
        var dealsPayload = """
        {
          "results": [
            {
              "id": "deal-avslag",
              "properties": {
                "dealname": "Rejected after contact mapping",
                "dealstage": "klar kund",
                "closedate": "1739980800000",
                "hs_lastmodifieddate": "1739980800000",
                "amount": "100",
                "deal_currency_code": "SEK"
              }
            }
          ]
        }
        """;

        var contactsPayload = """
        {
          "results": [
            {
              "id": "contact-avslag",
              "properties": {
                "saljare": "2875",
                "forsaljningsdatum": "2025-02-20",
                "kundstatus": "__KUNDSTATUS__"
              }
            }
          ]
        }
        """;

        var dealToContactAssociationsPayload = """
        {
          "results": [
            {
              "from": { "id": "deal-avslag" },
              "to": [
                { "toObjectId": "contact-avslag" }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(
            dealsPayload,
            contactsPayload.Replace("__KUNDSTATUS__", kundstatus),
            dealToContactAssociationsPayload: dealToContactAssociationsPayload);

        var result = await client.GetFulfilledDealsAsync(null, null, 100);

        var deal = Assert.Single(result.Deals);
        Assert.False(deal.IsFulfilled);
        Assert.Equal(kundstatus, deal.ContactKundstatus);
        Assert.Equal("2875", deal.SaljId);
        Assert.Equal(new DateTime(2025, 2, 20, 0, 0, 0, DateTimeKind.Utc), deal.FulfilledDateUtc);
    }

    [Fact]
    public async Task SearchFulfilledDealsByClosedDateRangeAsync_UsesContactDateWindow_AndRetainsAllowedLostDealsOnly()
    {
        var contactsPayload = """
        {
          "results": [
            {
              "id": "contact-1",
              "properties": {
                "saljare": "1234",
                "forsaljningsdatum": "2026-02-20"
              }
            },
            {
              "id": "contact-2",
              "properties": {
                "saljare": "5678",
                "forsaljningsdatum": "2026-02-21",
                "kundstatus": "Annullerat"
              }
            },
            {
              "id": "contact-3",
              "properties": {
                "saljare": "9999",
                "forsaljningsdatum": "2026-02-22",
                "kundstatus": "Annullerad"
              }
            }
          ]
        }
        """;

        var associationsPayload = """
        {
          "results": [
            {
              "from": { "id": "contact-1" },
              "to": [ { "toObjectId": 9001 } ]
            },
            {
              "from": { "id": "contact-2" },
              "to": [ { "toObjectId": 9002 } ]
            },
            {
              "from": { "id": "contact-3" },
              "to": [ { "toObjectId": 9003 } ]
            }
          ]
        }
        """;

        var dealsBatchPayload = """
        {
          "results": [
            {
              "id": "9001",
              "properties": {
                "dealname": "Qualified deal",
                "dealstage": "qualifiedtobuy",
                "closedate": "1739980800000",
                "hs_lastmodifieddate": "1739980800000",
                "amount": "100",
                "deal_currency_code": "SEK",
                "saljarprovision": "10.0"
              }
            },
            {
              "id": "9002",
              "properties": {
                "dealname": "Cancelled retained deal",
                "dealstage": "3832342762",
                "closedate": "1739980800000",
                "hs_lastmodifieddate": "1739980800000",
                "amount": "50",
                "deal_currency_code": "SEK",
                "saljarprovision": "5.0"
              }
            },
            {
              "id": "9003",
              "properties": {
                "dealname": "Cancelled excluded deal",
                "dealstage": "3832342762",
                "closedate": "1739980800000",
                "hs_lastmodifieddate": "1739980800000",
                "amount": "75",
                "deal_currency_code": "SEK",
                "saljarprovision": "7.5"
              }
            }
          ]
        }
        """;

        var dealToContactAssociationsPayload = """
        {
          "results": [
            {
              "from": { "id": "9001" },
              "to": [ { "toObjectId": "contact-1" } ]
            },
            {
              "from": { "id": "9002" },
              "to": [ { "toObjectId": "contact-2" } ]
            },
            {
              "from": { "id": "9003" },
              "to": [ { "toObjectId": "contact-3" } ]
            }
          ]
        }
        """;

        var pipelinePayload = """
        {
          "results": [
            {
              "id": "default",
              "label": "Sales Pipeline",
              "stages": [
                { "id": "qualifiedtobuy", "label": "Nyregistrerad" },
                { "id": "contractsent", "label": "Bokad" },
                { "id": "3347679468", "label": "Installerad" },
                { "id": "closedwon", "label": "Klar" },
                { "id": "3832342762", "label": "Förlorad" }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(
            dealsPayload: """{"results":[]}""",
            contactsPayload: contactsPayload,
            associationsPayload: associationsPayload,
            dealsBatchPayload: dealsBatchPayload,
            pipelinePayload: pipelinePayload,
            dealToContactAssociationsPayload: dealToContactAssociationsPayload);

        var result = await client.SearchFulfilledDealsByClosedDateRangeAsync(
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 28, 23, 59, 59, DateTimeKind.Utc),
            afterCursor: null,
            pageSize: 100);

        Assert.Equal(new[] { "9001", "9002" }, result.Deals.Select(d => d.ExternalDealId).ToArray());
        Assert.Equal("1234", result.Deals[0].SaljId);
        Assert.True(result.Deals[0].IsFulfilled);
        Assert.Equal("5678", result.Deals[1].SaljId);
        Assert.False(result.Deals[1].IsFulfilled);
        Assert.Equal("Annullerat", result.Deals[1].ContactKundstatus);
    }

    private static HubSpotClient CreateClient(
        string dealsPayload,
        string contactsPayload,
        string? associationsPayload = null,
        string? dealsBatchPayload = null,
        string? pipelinePayload = null,
        string? dealToContactAssociationsPayload = null)
    {
        var options = Options.Create(new HubSpotOptions
        {
            Enabled = true,
            AccessToken = "token",
            FulfilledProperty = "dealstage",
            FulfilledDateProperty = "forsaljningsdatum",
            DealFallbackDateProperty = "closedate",
            LastModifiedProperty = "hs_lastmodifieddate",
            DealNameProperty = "dealname",
            OwnerEmailProperty = "email",
            OwnerIdProperty = "hubspot_owner_id",
            SaljIdProperty = "saljid",
            ContactSaljareProperty = "saljare",
            AmountProperty = "amount",
            CurrencyCodeProperty = "deal_currency_code",
            ProvisionProperty = "saljarprovision",
            FulfilledValues =
            [
                "nyregistrerad",
                "ombokning",
                "bokad",
                "klar kund",
                "installerad - ej fakturerad"
            ]
        });

        var httpClient = new HttpClient(new StubHttpMessageHandler(
            dealsPayload,
            contactsPayload,
            associationsPayload,
            dealsBatchPayload,
            pipelinePayload,
            dealToContactAssociationsPayload))
        {
            BaseAddress = new Uri("https://api.hubapi.com")
        };

        return new HubSpotClient(httpClient, options, NullLogger<HubSpotClient>.Instance);
    }

    private sealed class StubHttpMessageHandler(
        string dealsPayload,
        string contactsPayload,
        string? associationsPayload = null,
        string? dealsBatchPayload = null,
        string? pipelinePayload = null,
        string? dealToContactAssociationsPayload = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string payload;

            if (request.Method == HttpMethod.Get && path == "/crm/v3/objects/deals")
            {
                payload = dealsPayload;
            }
            else if (request.Method == HttpMethod.Post && path == "/crm/v3/objects/contacts/batch/read")
            {
                payload = contactsPayload;
            }
            else if (request.Method == HttpMethod.Post && path == "/crm/v3/objects/contacts/search")
            {
                payload = contactsPayload;
            }
            else if (request.Method == HttpMethod.Post && path == "/crm/v4/associations/contacts/deals/batch/read")
            {
                payload = associationsPayload ?? """{"results":[]}""";
            }
            else if (request.Method == HttpMethod.Post && path == "/crm/v4/associations/deals/contacts/batch/read")
            {
                payload = dealToContactAssociationsPayload ?? """{"results":[]}""";
            }
            else if (request.Method == HttpMethod.Post && path == "/crm/v3/objects/deals/batch/read")
            {
                payload = dealsBatchPayload ?? """{"results":[]}""";
            }
            else if (request.Method == HttpMethod.Get && path.StartsWith("/crm/v3/properties/deals/"))
            {
                payload = "{}";
            }
            else if (request.Method == HttpMethod.Get && path == "/crm/v3/pipelines/deals")
            {
                payload = pipelinePayload ?? "{\"results\":[]}";
            }
            else if (request.Method == HttpMethod.Get && path.Contains("/associations/contacts", StringComparison.Ordinal))
            {
                payload = "{\"results\":[]}";
            }
            else
            {
                payload = "{}";
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
