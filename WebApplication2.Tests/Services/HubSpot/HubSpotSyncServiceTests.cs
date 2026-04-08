using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebApplication2.Models;
using WebApplication2.Services.HubSpot;
using WebApplication2.Tests.Helpers;
using Xunit;

namespace WebApplication2.Tests.Services.HubSpot;

public class HubSpotSyncServiceTests
{
    [Fact]
    public async Task RebuildCurrentMonthOnlyAsync_ClearsExistingHubSpotData_AndImportsCurrentMonthDeals()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("5555", "5555@stl.nu");

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "old-deal",
            SaljId = "5555",
            OwnerEmail = "old@stl.nu",
            OwnerUserId = user.Id,
            FulfilledDateUtc = DateTime.UtcNow.AddMonths(-2),
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "new-deal",
                            OwnerId = "owner-new",
                            SaljId = "5555",
                            OwnerEmail = null,
                            IsFulfilled = true,
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "hash-new"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RebuildCurrentMonthOnlyAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);
        Assert.Equal("new-deal", (await env.Context.HubSpotDealImports.SingleAsync()).ExternalDealId);
    }

    [Fact]
    public async Task RebuildCurrentMonthOnlyAsync_RetainsAllowedLostDealsReturnedBySearchWindow()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("5555", "5555@stl.nu");

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "fulfilled-deal",
                            SaljId = "5555",
                            OwnerEmail = "5555@stl.nu",
                            IsFulfilled = true,
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "hash-fulfilled"
                        },
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "lost-deal",
                            SaljId = "5555",
                            OwnerEmail = "5555@stl.nu",
                            IsFulfilled = false,
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            ContactKundstatus = "Annullerat",
                            PayloadHash = "hash-lost"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RebuildCurrentMonthOnlyAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.DealsImported);

        var deals = await env.Context.HubSpotDealImports
            .OrderBy(d => d.ExternalDealId)
            .ToListAsync();

        Assert.Equal(new[] { "fulfilled-deal", "lost-deal" }, deals.Select(d => d.ExternalDealId).ToArray());
        Assert.True(deals.Single(d => d.ExternalDealId == "fulfilled-deal").IsFulfilled);
        Assert.False(deals.Single(d => d.ExternalDealId == "lost-deal").IsFulfilled);
    }

    [Fact]
    public async Task BackfillLineItemsAsync_UpdatesOnlyMatchingDealsWithoutClearingImportedRows()
    {
        using var env = TestIdentityEnvironment.Create();
        var nowUtc = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);

        env.Context.HubSpotDealImports.AddRange(
            new HubSpotDealImport
            {
                ExternalDealId = "missing-line-items",
                SaljId = "5555",
                OwnerEmail = "5555@stl.nu",
                FulfilledDateUtc = nowUtc,
                FirstSeenUtc = nowUtc,
                LastSeenUtc = nowUtc,
                LineItemsJson = null
            },
            new HubSpotDealImport
            {
                ExternalDealId = "already-hydrated",
                SaljId = "5555",
                OwnerEmail = "5555@stl.nu",
                FulfilledDateUtc = nowUtc,
                FirstSeenUtc = nowUtc,
                LastSeenUtc = nowUtc,
                LineItemsJson = """[{"LineItemId":"existing"}]"""
            });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            enrichDealsWithLineItemsAsync: deals =>
            {
                var deal = Assert.Single(deals);
                Assert.Equal("missing-line-items", deal.ExternalDealId);
                deal.LineItems =
                [
                    new HubSpotDealLineItemRecord
                    {
                        LineItemId = "line-item-1",
                        Name = "Kamera",
                        Quantity = 1,
                        Price = 2200,
                        Amount = 2200,
                        Sku = "CAM-01"
                    }
                ];

                return Task.CompletedTask;
            });

        var sut = CreateSut(env, client);

        var result = await sut.BackfillLineItemsAsync(
            fulfilledDateFromUtc: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            fulfilledDateToUtc: new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc),
            missingOnly: true);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsFetched);
        Assert.Equal(1, result.DealsUpdated);

        var deals = await env.Context.HubSpotDealImports
            .OrderBy(d => d.ExternalDealId)
            .ToListAsync();

        Assert.Equal(2, deals.Count);
        Assert.Contains("\"LineItemId\":\"line-item-1\"", deals.Single(d => d.ExternalDealId == "missing-line-items").LineItemsJson);
        Assert.Equal("""[{"LineItemId":"existing"}]""", deals.Single(d => d.ExternalDealId == "already-hydrated").LineItemsJson);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_SkipsDealWhenSaljIdIsMissing()
    {
        using var env = TestIdentityEnvironment.Create();

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-no-owner-id",
                            OwnerId = null,
                            OwnerEmail = "5555@stl.nu",
                            DealName = "Missing owner id",
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 90m,
                            CurrencyCode = "SEK",
                            PayloadHash = "hash-missing-owner-id"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsSkipped);
        Assert.Empty(await env.Context.HubSpotDealImports.ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_ImportsDealWhenSaljIdExists()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("5555", "5555@stl.nu");

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-no-owner-id-with-saljid",
                            OwnerId = null,
                            SaljId = "5555",
                            OwnerEmail = "unknown@stl.nu",
                            DealName = "Missing owner id but has saljid",
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 90m,
                            CurrencyCode = "SEK",
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "hash-missing-owner-id-with-saljid"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);
        Assert.Equal(0, result.DealsSkipped);

        var importedDeal = await env.Context.HubSpotDealImports.SingleAsync();
        Assert.Equal("5555", importedDeal.SaljId);
        Assert.Equal(user.Id, importedDeal.OwnerUserId);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_ImportsDealWhenSaljIdExists_WithMissingOwnerEmail()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("3333", "3333@stl.nu");

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-2",
                            OwnerId = "owner-2",
                            OwnerEmail = null,
                            SaljId = "3333",
                            DealName = "SaljId only",
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 250m,
                            CurrencyCode = "SEK",
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "hash-b"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);
        Assert.Equal(0, result.DealsSkipped);

        var deal = await env.Context.HubSpotDealImports.SingleAsync(d => d.ExternalDealId == "deal-2");
        Assert.Equal(user.Id, deal.OwnerUserId);
        Assert.Equal("3333", deal.SaljId);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_SkipsDealsAtOrBelowConfiguredMinimumAmount()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("5555", "5555@stl.nu");

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-at-threshold",
                            SaljId = "5555",
                            OwnerEmail = "5555@stl.nu",
                            IsFulfilled = true,
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 15000m,
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "hash-at-threshold"
                        },
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-above-threshold",
                            SaljId = "5555",
                            OwnerEmail = "5555@stl.nu",
                            IsFulfilled = true,
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 15001m,
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "hash-above-threshold"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client, options => options.MinimumDealAmount = 15000m);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);
        Assert.Equal(1, result.DealsSkipped);
        Assert.Equal(
            new[] { "deal-above-threshold" },
            (await env.Context.HubSpotDealImports.OrderBy(d => d.ExternalDealId).ToListAsync())
                .Select(d => d.ExternalDealId)
                .ToArray());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RemovesExistingDealWhenUpdatedAmountFallsBelowConfiguredMinimum()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("5555", "5555@stl.nu");

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "deal-drops-below-threshold",
            SaljId = "5555",
            OwnerEmail = "5555@stl.nu",
            OwnerUserId = user.Id,
            IsFulfilled = true,
            FulfilledDateUtc = DateTime.UtcNow,
            Amount = 18000m,
            ContactKundstatus = "Klar kund",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-drops-below-threshold",
                            SaljId = "5555",
                            OwnerEmail = "5555@stl.nu",
                            IsFulfilled = true,
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 14999m,
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "hash-below-threshold"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client, options => options.MinimumDealAmount = 15000m);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsUpdated);
        Assert.Empty(await env.Context.HubSpotDealImports.ToListAsync());
    }

    [Fact]
    public async Task PurgeDisqualifiedDealsAsync_RemovesRowsAtOrBelowConfiguredMinimum()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("5555", "5555@stl.nu");

        env.Context.HubSpotDealImports.AddRange(
            new HubSpotDealImport
            {
                ExternalDealId = "remove-null",
                SaljId = "5555",
                OwnerEmail = "5555@stl.nu",
                IsFulfilled = true,
                FulfilledDateUtc = DateTime.UtcNow,
                Amount = null,
                ContactKundstatus = "Klar kund",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "remove-threshold",
                SaljId = "5555",
                OwnerEmail = "5555@stl.nu",
                IsFulfilled = true,
                FulfilledDateUtc = DateTime.UtcNow,
                Amount = 15000m,
                ContactKundstatus = "Klar kund",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "keep-above-threshold",
                SaljId = "5555",
                OwnerEmail = "5555@stl.nu",
                IsFulfilled = true,
                FulfilledDateUtc = DateTime.UtcNow,
                Amount = 15001m,
                ContactKundstatus = "Klar kund",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            });
        await env.Context.SaveChangesAsync();

        var sut = CreateSut(env, new FakeHubSpotClient(), options => options.MinimumDealAmount = 15000m);

        var result = await sut.PurgeDisqualifiedDealsAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.DealsUpdated);
        Assert.Equal(
            new[] { "keep-above-threshold" },
            (await env.Context.HubSpotDealImports.OrderBy(d => d.ExternalDealId).ToListAsync())
                .Select(d => d.ExternalDealId)
                .ToArray());
    }

    [Fact]
    public async Task PurgeDisqualifiedDealsAsync_RecalculatesActiveContestEntries()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("1234", "1234@stl.nu");

        env.Context.Contests.Add(new Contest
        {
            Name = "Active Threshold Contest",
            StartDate = DateTime.Now.AddDays(-1),
            EndDate = DateTime.Now.AddDays(1),
            IsActive = true,
            CreatedDate = DateTime.Now
        });

        env.Context.HubSpotDealImports.AddRange(
            new HubSpotDealImport
            {
                ExternalDealId = "counted-1",
                SaljId = "1234",
                OwnerEmail = "1234@stl.nu",
                OwnerUserId = user.Id,
                IsFulfilled = true,
                FulfilledDateUtc = DateTime.UtcNow,
                Amount = 20000m,
                ContactKundstatus = "Klar kund",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "removed-1",
                SaljId = "1234",
                OwnerEmail = "1234@stl.nu",
                OwnerUserId = user.Id,
                IsFulfilled = true,
                FulfilledDateUtc = DateTime.UtcNow,
                Amount = 12000m,
                ContactKundstatus = "Klar kund",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            });
        await env.Context.SaveChangesAsync();

        var sut = CreateSut(env, new FakeHubSpotClient(), options => options.MinimumDealAmount = 15000m);

        var result = await sut.PurgeDisqualifiedDealsAsync();

        Assert.True(result.Succeeded);

        var contestEntry = await env.Context.ContestEntries.SingleAsync();
        Assert.Equal(user.Id, contestEntry.UserId);
        Assert.Equal(1, contestEntry.DealsCount);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_SkipsDealWhenNoSaljId()
    {
        using var env = TestIdentityEnvironment.Create();

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-3",
                            OwnerId = "owner-3",
                            OwnerEmail = null,
                            DealName = "Unresolved owner",
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 80m,
                            CurrencyCode = "SEK",
                            PayloadHash = "hash-c"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsSkipped);
        Assert.Equal(0, result.DealsImported);
        Assert.Empty(await env.Context.HubSpotDealImports.ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RemovesPreviouslyImportedDeal_WhenNoLongerFulfilled()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("7777", "7777@stl.nu");

        var fulfilledAt = DateTime.UtcNow.AddMinutes(-5);
        var unfulfilledAt = DateTime.UtcNow;

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-redacted",
                            OwnerId = "owner-redacted",
                            OwnerEmail = "7777@stl.nu",
                            SaljId = "7777",
                            IsFulfilled = true,
                            DealName = "Initially fulfilled",
                            FulfilledDateUtc = fulfilledAt,
                            LastModifiedUtc = fulfilledAt,
                            Amount = 120m,
                            CurrencyCode = "SEK",
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "hash-fulfilled"
                        }
                    ]
                },
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-redacted",
                            OwnerId = "owner-redacted",
                            OwnerEmail = "7777@stl.nu",
                            IsFulfilled = false,
                            DealName = "Now redacted",
                            FulfilledDateUtc = null,
                            LastModifiedUtc = unfulfilledAt,
                            Amount = 120m,
                            CurrencyCode = "SEK",
                            PayloadHash = "hash-unfulfilled"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var firstRun = await sut.RunIncrementalSyncAsync();
        var secondRun = await sut.RunIncrementalSyncAsync();

        Assert.True(firstRun.Succeeded);
        Assert.Equal(1, firstRun.DealsImported);

        Assert.True(secondRun.Succeeded);
        Assert.Empty(await env.Context.HubSpotDealImports
            .Where(d => d.ExternalDealId == "deal-redacted")
            .ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RemovesPreviouslyImportedDeal_WhenResolvedSaljIdIsMissing()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("7777", "7777@stl.nu");

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "deal-missing-saljid",
            SaljId = "7777",
            OwnerEmail = "7777@stl.nu",
            OwnerUserId = user.Id,
            IsFulfilled = true,
            FulfilledDateUtc = DateTime.UtcNow.AddMinutes(-5),
            ContactKundstatus = "Klar kund",
            FirstSeenUtc = DateTime.UtcNow.AddMinutes(-5),
            LastSeenUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-missing-saljid",
                            OwnerEmail = "7777@stl.nu",
                            SaljId = null,
                            IsFulfilled = true,
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "missing-saljid"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(await env.Context.HubSpotDealImports
            .Where(d => d.ExternalDealId == "deal-missing-saljid")
            .ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RetainsCancelledDeal_WhenKundstatusIsAnnullerat()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("7777", "7777@stl.nu");

        var cancelledAt = DateTime.UtcNow;

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-lost",
                            OwnerId = "owner-lost",
                            OwnerEmail = "7777@stl.nu",
                            SaljId = "7777",
                            IsFulfilled = false,
                            DealName = "Cancelled deal",
                            FulfilledDateUtc = cancelledAt,
                            LastModifiedUtc = cancelledAt,
                            Amount = 120m,
                            SellerProvision = 12m,
                            CurrencyCode = "SEK",
                            ContactKundstatus = "Annullerat",
                            PayloadHash = "hash-lost"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);

        var importedDeal = await env.Context.HubSpotDealImports.SingleAsync(d => d.ExternalDealId == "deal-lost");
        Assert.False(importedDeal.IsFulfilled);
        Assert.Equal("Annullerat", importedDeal.ContactKundstatus);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_DoesNotRetainCancelledDeal_WhenKundstatusIsAnnullerad()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("7777", "7777@stl.nu");

        var cancelledAt = DateTime.UtcNow;

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-annullerad",
                            OwnerEmail = "7777@stl.nu",
                            SaljId = "7777",
                            IsFulfilled = false,
                            DealName = "Excluded cancelled deal",
                            FulfilledDateUtc = cancelledAt,
                            LastModifiedUtc = cancelledAt,
                            ContactKundstatus = "Annullerad",
                            PayloadHash = "hash-annullerad"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsSkipped);
        Assert.Empty(await env.Context.HubSpotDealImports.ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RemovesPreviouslyImportedDeal_WhenStoredKundstatusIsAvslag()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("2875", "2875@stl.nu");

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "deal-avslag-stale",
            SaljId = "2875",
            OwnerEmail = "2875@stl.nu",
            OwnerUserId = user.Id,
            IsFulfilled = true,
            FulfilledDateUtc = DateTime.UtcNow,
            ContactKundstatus = "Avslag",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult()
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(await env.Context.HubSpotDealImports
            .Where(d => d.ExternalDealId == "deal-avslag-stale")
            .ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_DemotesPreviouslyImportedDeal_WhenStoredKundstatusIsAnnullerat()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("2875", "2875@stl.nu");

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "deal-annullerat-stale",
            SaljId = "2875",
            OwnerEmail = "2875@stl.nu",
            OwnerUserId = user.Id,
            IsFulfilled = true,
            FulfilledDateUtc = DateTime.UtcNow.AddMonths(-1),
            ContactKundstatus = "Annullerat",
            FirstSeenUtc = DateTime.UtcNow.AddMonths(-1),
            LastSeenUtc = DateTime.UtcNow.AddMonths(-1)
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult()
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);

        var deal = await env.Context.HubSpotDealImports
            .SingleAsync(d => d.ExternalDealId == "deal-annullerat-stale");
        Assert.False(deal.IsFulfilled);
        Assert.Equal("Annullerat", deal.ContactKundstatus);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RemovesPreviouslyImportedDeal_WhenStoredKundstatusIsNotFulfilledOrRetained()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("2875", "2875@stl.nu");

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "deal-ej-svar-stale",
            SaljId = "2875",
            OwnerEmail = "2875@stl.nu",
            OwnerUserId = user.Id,
            IsFulfilled = true,
            FulfilledDateUtc = DateTime.UtcNow,
            ContactKundstatus = "Ej svar",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult()
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(await env.Context.HubSpotDealImports
            .Where(d => d.ExternalDealId == "deal-ej-svar-stale")
            .ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RetainsLostDeal_WhenKundstatusIsWinback()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("7777", "7777@stl.nu");

        var lostAt = DateTime.UtcNow;

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-winback",
                            OwnerId = "owner-winback",
                            OwnerEmail = "7777@stl.nu",
                            SaljId = "7777",
                            IsFulfilled = false,
                            DealName = "Winback deal",
                            FulfilledDateUtc = lostAt,
                            LastModifiedUtc = lostAt,
                            Amount = 120m,
                            SellerProvision = 12m,
                            CurrencyCode = "SEK",
                            ContactKundstatus = "winback",
                            PayloadHash = "hash-winback"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);

        var importedDeal = await env.Context.HubSpotDealImports.SingleAsync(d => d.ExternalDealId == "deal-winback");
        Assert.False(importedDeal.IsFulfilled);
        Assert.Equal("winback", importedDeal.ContactKundstatus);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RetainsLostDeal_WhenKundstatusIsSaljare()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("7777", "7777@stl.nu");

        var lostAt = DateTime.UtcNow;

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-saljare",
                            OwnerId = "owner-saljare",
                            OwnerEmail = "7777@stl.nu",
                            SaljId = "7777",
                            IsFulfilled = false,
                            DealName = "Saljare deal",
                            FulfilledDateUtc = lostAt,
                            LastModifiedUtc = lostAt,
                            Amount = 120m,
                            SellerProvision = 12m,
                            CurrencyCode = "SEK",
                            ContactKundstatus = "Säljare",
                            PayloadHash = "hash-saljare"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);

        var importedDeal = await env.Context.HubSpotDealImports.SingleAsync(d => d.ExternalDealId == "deal-saljare");
        Assert.False(importedDeal.IsFulfilled);
        Assert.Equal("Säljare", importedDeal.ContactKundstatus);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RetainsLostDeal_WhenKundstatusIsAnnullerat()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("7777", "7777@stl.nu");

        var lostAt = DateTime.UtcNow;

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "deal-annullerat",
                            OwnerId = "owner-annullerat",
                            OwnerEmail = "7777@stl.nu",
                            SaljId = "7777",
                            IsFulfilled = false,
                            DealName = "Annullerat deal",
                            FulfilledDateUtc = lostAt,
                            LastModifiedUtc = lostAt,
                            Amount = 120m,
                            SellerProvision = 12m,
                            CurrencyCode = "SEK",
                            ContactKundstatus = "Annullerat",
                            PayloadHash = "hash-annullerat"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);

        var importedDeal = await env.Context.HubSpotDealImports.SingleAsync(d => d.ExternalDealId == "deal-annullerat");
        Assert.False(importedDeal.IsFulfilled);
        Assert.Equal("Annullerat", importedDeal.ContactKundstatus);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_UpsertsExistingDealIdempotently()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("4444", "4444@stl.nu");

        var firstSyncTime = DateTime.UtcNow.AddMinutes(-10);
        var secondSyncTime = DateTime.UtcNow;

        var incrementalCallCount = 0;
        var client = new FakeHubSpotClient(
            getFulfilledDeals: (_, _, _) =>
            {
                incrementalCallCount++;

                return incrementalCallCount switch
                {
                    1 => new HubSpotDealsPageResult
                    {
                        Deals =
                        [
                            new HubSpotDealRecord
                            {
                                ExternalDealId = "deal-9",
                                OwnerId = "owner-9",
                                OwnerEmail = "4444@stl.nu",
                                SaljId = "4444",
                                DealName = "Original",
                                FulfilledDateUtc = firstSyncTime,
                                LastModifiedUtc = firstSyncTime,
                                Amount = 100m,
                                SellerProvision = 10m,
                                CurrencyCode = "SEK",
                                DealStage = "closedwon",
                                ContactKundstatus = "Klar kund",
                                PayloadHash = "payload-v1"
                            }
                        ]
                    },
                    2 => new HubSpotDealsPageResult
                    {
                        Deals =
                        [
                            new HubSpotDealRecord
                            {
                                ExternalDealId = "deal-9",
                                OwnerId = "owner-9",
                                OwnerEmail = "4444@stl.nu",
                                SaljId = "4444",
                                DealName = "Updated",
                                FulfilledDateUtc = secondSyncTime,
                                LastModifiedUtc = secondSyncTime,
                                Amount = 250m,
                                SellerProvision = 25m,
                                CurrencyCode = "SEK",
                                DealStage = "closedwon",
                                ContactKundstatus = "Klar kund",
                                PayloadHash = "payload-v2"
                            }
                        ]
                    },
                    _ => new HubSpotDealsPageResult()
                };
            },
            searchDealsByClosedDateRange: (_, _, _, _) => new HubSpotDealsPageResult());

        var sut = CreateSut(env, client);

        var firstRun = await sut.RunIncrementalSyncAsync();
        var secondRun = await sut.RunIncrementalSyncAsync();

        Assert.True(firstRun.Succeeded);
        Assert.Equal(1, firstRun.DealsImported);
        Assert.Equal(0, firstRun.DealsUpdated);

        Assert.True(secondRun.Succeeded);
        Assert.Equal(0, secondRun.DealsImported);
        Assert.Equal(1, secondRun.DealsUpdated);

        var deals = await env.Context.HubSpotDealImports.Where(d => d.ExternalDealId == "deal-9").ToListAsync();
        Assert.Single(deals);

        var deal = deals.Single();
        Assert.Equal(user.Id, deal.OwnerUserId);
        Assert.Equal("Updated", deal.DealName);
        Assert.Equal(250m, deal.Amount);
        Assert.Equal(25m, deal.SellerProvision);
        Assert.Equal("payload-v2", deal.PayloadHash);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_ContinuesFromStoredCursor_BeforeAdvancingSuccessWatermark()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("5555", "5555@stl.nu");

        var previousSuccessUtc = new DateTime(2026, 03, 18, 15, 00, 00, DateTimeKind.Utc);
        env.Context.HubSpotSyncStates.Add(new HubSpotSyncState
        {
            IntegrationName = "HubSpotDeals",
            LastSuccessfulSyncUtc = previousSuccessUtc
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            getFulfilledDeals: (modifiedSinceUtc, afterCursor, _) =>
            {
                Assert.Equal(previousSuccessUtc, modifiedSinceUtc);

                return afterCursor switch
                {
                    null => new HubSpotDealsPageResult
                    {
                        Deals =
                        [
                            new HubSpotDealRecord
                            {
                                ExternalDealId = "deal-page-1",
                                SaljId = "5555",
                                OwnerEmail = "5555@stl.nu",
                                IsFulfilled = true,
                                FulfilledDateUtc = previousSuccessUtc.AddMinutes(1),
                                LastModifiedUtc = previousSuccessUtc.AddMinutes(1),
                                ContactKundstatus = "Klar kund",
                                PayloadHash = "page-1"
                            }
                        ],
                        NextCursor = "cursor-1"
                    },
                    "cursor-1" => new HubSpotDealsPageResult
                    {
                        Deals =
                        [
                            new HubSpotDealRecord
                            {
                                ExternalDealId = "deal-page-2",
                                SaljId = "5555",
                                OwnerEmail = "5555@stl.nu",
                                IsFulfilled = true,
                                FulfilledDateUtc = previousSuccessUtc.AddMinutes(2),
                                LastModifiedUtc = previousSuccessUtc.AddMinutes(2),
                                ContactKundstatus = "Klar kund",
                                PayloadHash = "page-2"
                            }
                        ]
                    },
                    _ => new HubSpotDealsPageResult()
                };
            });

        var sut = CreateSut(env, client);

        var firstRun = await sut.RunIncrementalSyncAsync();
        var stateAfterFirstRun = await env.Context.HubSpotSyncStates.SingleAsync(s => s.IntegrationName == "HubSpotDeals");

        Assert.True(firstRun.Succeeded);
        Assert.Equal(1, firstRun.DealsImported);
        Assert.Equal(previousSuccessUtc, stateAfterFirstRun.LastSuccessfulSyncUtc);
        Assert.Equal("cursor-1", stateAfterFirstRun.LastCursor);

        var secondRun = await sut.RunIncrementalSyncAsync();
        var stateAfterSecondRun = await env.Context.HubSpotSyncStates.SingleAsync(s => s.IntegrationName == "HubSpotDeals");

        Assert.True(secondRun.Succeeded);
        Assert.Equal(1, secondRun.DealsImported);
        Assert.True(stateAfterSecondRun.LastSuccessfulSyncUtc > previousSuccessUtc);
        Assert.Null(stateAfterSecondRun.LastCursor);

        var importedDeals = await env.Context.HubSpotDealImports
            .OrderBy(d => d.ExternalDealId)
            .Select(d => d.ExternalDealId)
            .ToListAsync();

        Assert.Equal(new[] { "deal-page-1", "deal-page-2" }, importedDeals);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_ClampsOldIncrementalBacklogToConfiguredLookback()
    {
        using var env = TestIdentityEnvironment.Create();
        await env.CreateUserAsync("5555", "5555@stl.nu");

        env.Context.HubSpotSyncStates.Add(new HubSpotSyncState
        {
            IntegrationName = "HubSpotDeals",
            LastSuccessfulSyncUtc = DateTime.UtcNow.AddDays(-120),
            LastCursor = "stale-cursor"
        });
        await env.Context.SaveChangesAsync();

        DateTime? firstCallModifiedSinceUtc = null;
        string? firstCallCursor = null;
        DateTime? secondCallModifiedSinceUtc = null;
        string? secondCallCursor = null;

        var client = new FakeHubSpotClient(
            getFulfilledDeals: (modifiedSinceUtc, afterCursor, _) =>
            {
                if (firstCallModifiedSinceUtc == null)
                {
                    firstCallModifiedSinceUtc = modifiedSinceUtc;
                    firstCallCursor = afterCursor;
                    return new HubSpotDealsPageResult
                    {
                        Deals =
                        [
                            new HubSpotDealRecord
                            {
                                ExternalDealId = "clamped-deal-1",
                                SaljId = "5555",
                                OwnerEmail = "5555@stl.nu",
                                IsFulfilled = true,
                                FulfilledDateUtc = DateTime.UtcNow,
                                LastModifiedUtc = DateTime.UtcNow,
                                ContactKundstatus = "Klar kund",
                                PayloadHash = "clamped-1"
                            }
                        ],
                        NextCursor = "fresh-cursor"
                    };
                }

                secondCallModifiedSinceUtc = modifiedSinceUtc;
                secondCallCursor = afterCursor;
                return new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "clamped-deal-2",
                            SaljId = "5555",
                            OwnerEmail = "5555@stl.nu",
                            IsFulfilled = true,
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            ContactKundstatus = "Klar kund",
                            PayloadHash = "clamped-2"
                        }
                    ]
                };
            });

        var sut = CreateSut(env, client, options =>
        {
            options.IncrementalCatchUpLookbackDays = 30;
        });

        var firstRun = await sut.RunIncrementalSyncAsync();
        var stateAfterFirstRun = await env.Context.HubSpotSyncStates.SingleAsync(s => s.IntegrationName == "HubSpotDeals");

        Assert.True(firstRun.Succeeded);
        Assert.NotNull(firstCallModifiedSinceUtc);
        Assert.Null(firstCallCursor);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(-30), firstCallModifiedSinceUtc);
        Assert.Equal("fresh-cursor", stateAfterFirstRun.LastCursor);

        var secondRun = await sut.RunIncrementalSyncAsync();

        Assert.True(secondRun.Succeeded);
        Assert.Equal("fresh-cursor", secondCallCursor);
        Assert.Equal(firstCallModifiedSinceUtc, secondCallModifiedSinceUtc);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RefreshesExistingCurrentMonthDeal_FromLiveMonthWindow()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("2875", "2875@stl.nu");

        var nowUtc = DateTime.UtcNow;
        var monthStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEndUtc = monthStartUtc.AddMonths(1).AddTicks(-1);
        var existingDealDateUtc = monthStartUtc.AddDays(2);

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "current-month-live-refresh",
            SaljId = "2875",
            OwnerEmail = "2875@stl.nu",
            OwnerUserId = user.Id,
            IsFulfilled = true,
            FulfilledDateUtc = existingDealDateUtc,
            ContactKundstatus = "Klar kund",
            FirstSeenUtc = existingDealDateUtc,
            LastSeenUtc = existingDealDateUtc
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            getFulfilledDeals: (_, _, _) => new HubSpotDealsPageResult(),
            searchDealsByClosedDateRange: (closedDateStartUtc, closedDateEndUtc, _, _) =>
            {
                Assert.Equal(monthStartUtc, closedDateStartUtc);
                Assert.Equal(monthEndUtc, closedDateEndUtc);

                return new HubSpotDealsPageResult
                {
                    Deals =
                    [
                        new HubSpotDealRecord
                        {
                            ExternalDealId = "current-month-live-refresh",
                            SaljId = "2875",
                            OwnerEmail = "2875@stl.nu",
                            IsFulfilled = false,
                            FulfilledDateUtc = existingDealDateUtc,
                            LastModifiedUtc = nowUtc,
                            ContactKundstatus = "Annullerat",
                            PayloadHash = "live-month-refresh"
                        }
                    ]
                };
            });

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);

        var refreshedDeal = await env.Context.HubSpotDealImports
            .SingleAsync(d => d.ExternalDealId == "current-month-live-refresh");

        Assert.False(refreshedDeal.IsFulfilled);
        Assert.Equal("Annullerat", refreshedDeal.ContactKundstatus);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_PrunesStaleDealsAfterCompletedCurrentMonthLiveSweep()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("2875", "2875@stl.nu");

        var nowUtc = DateTime.UtcNow;
        var monthStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEndUtc = monthStartUtc.AddMonths(1).AddTicks(-1);
        var staleDealDateUtc = monthStartUtc.AddDays(1);

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "stale-current-month-deal",
            SaljId = "2875",
            OwnerEmail = "2875@stl.nu",
            OwnerUserId = user.Id,
            IsFulfilled = true,
            FulfilledDateUtc = staleDealDateUtc,
            ContactKundstatus = "Nyregistrerad",
            FirstSeenUtc = staleDealDateUtc,
            LastSeenUtc = staleDealDateUtc
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            getFulfilledDeals: (_, _, _) => new HubSpotDealsPageResult(),
            searchDealsByClosedDateRange: (closedDateStartUtc, closedDateEndUtc, _, _) =>
            {
                Assert.Equal(monthStartUtc, closedDateStartUtc);
                Assert.Equal(monthEndUtc, closedDateEndUtc);
                return new HubSpotDealsPageResult();
            });

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(await env.Context.HubSpotDealImports
            .Where(d => d.ExternalDealId == "stale-current-month-deal")
            .ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_ContinuesActiveContestWindowAcrossRuns()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("1234", "1234@stl.nu");

        env.Context.Contests.Add(new Contest
        {
            Name = "Cursor Test Contest",
            StartDate = DateTime.Now.AddDays(-1),
            EndDate = DateTime.Now.AddDays(1),
            IsActive = true,
            CreatedDate = DateTime.Now
        });
        await env.Context.SaveChangesAsync();

        var contestDealTimeUtc = DateTime.UtcNow.AddMinutes(-15);
        var client = new FakeHubSpotClient(
            getFulfilledDeals: (_, _, _) => new HubSpotDealsPageResult(),
            searchDealsByClosedDateRange: (_, _, afterCursor, _) =>
            {
                return afterCursor switch
                {
                    null => new HubSpotDealsPageResult
                    {
                        Deals =
                        [
                            new HubSpotDealRecord
                            {
                                ExternalDealId = "contest-deal-1",
                                SaljId = "1234",
                                OwnerEmail = "1234@stl.nu",
                                IsFulfilled = true,
                                FulfilledDateUtc = contestDealTimeUtc,
                                LastModifiedUtc = contestDealTimeUtc,
                                ContactKundstatus = "Klar kund",
                                PayloadHash = "contest-1"
                            }
                        ],
                        NextCursor = "contest-cursor-1"
                    },
                    "contest-cursor-1" => new HubSpotDealsPageResult
                    {
                        Deals =
                        [
                            new HubSpotDealRecord
                            {
                                ExternalDealId = "contest-deal-2",
                                SaljId = "1234",
                                OwnerEmail = "1234@stl.nu",
                                IsFulfilled = true,
                                FulfilledDateUtc = contestDealTimeUtc.AddMinutes(1),
                                LastModifiedUtc = contestDealTimeUtc.AddMinutes(1),
                                ContactKundstatus = "Klar kund",
                                PayloadHash = "contest-2"
                            }
                        ]
                    },
                    _ => new HubSpotDealsPageResult()
                };
            });

        var sut = CreateSut(env, client);

        var firstRun = await sut.RunIncrementalSyncAsync();
        var contestStateAfterFirstRun = await env.Context.HubSpotSyncStates
            .SingleAsync(s => s.IntegrationName.StartsWith("HubSpotDealsContest:"));

        Assert.True(firstRun.Succeeded);
        Assert.Equal("contest-cursor-1", contestStateAfterFirstRun.LastCursor);

        var secondRun = await sut.RunIncrementalSyncAsync();
        var contestStateAfterSecondRun = await env.Context.HubSpotSyncStates
            .SingleAsync(s => s.IntegrationName.StartsWith("HubSpotDealsContest:"));

        Assert.True(secondRun.Succeeded);
        Assert.Null(contestStateAfterSecondRun.LastCursor);
        Assert.True(contestStateAfterSecondRun.LastSuccessfulSyncUtc.HasValue);

        var importedDeals = await env.Context.HubSpotDealImports
            .Where(d => d.SaljId == "1234")
            .OrderBy(d => d.ExternalDealId)
            .ToListAsync();

        Assert.Equal(new[] { "contest-deal-1", "contest-deal-2" }, importedDeals.Select(d => d.ExternalDealId).ToArray());

        var contestEntry = await env.Context.ContestEntries.SingleAsync();
        Assert.Equal(user.Id, contestEntry.UserId);
        Assert.Equal(2, contestEntry.DealsCount);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_PrunesStaleDealsAfterCompletedContestWindowSweep()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("2875", "2875@stl.nu");

        env.Context.Contests.Add(new Contest
        {
            Name = "Prune Contest",
            StartDate = DateTime.Now.AddDays(-1),
            EndDate = DateTime.Now.AddDays(1),
            IsActive = true,
            CreatedDate = DateTime.Now
        });

        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "stale-window-deal",
            SaljId = "2875",
            OwnerEmail = "2875@stl.nu",
            OwnerUserId = user.Id,
            IsFulfilled = true,
            FulfilledDateUtc = DateTime.UtcNow.Date,
            ContactKundstatus = "Nyregistrerad",
            FirstSeenUtc = DateTime.UtcNow.AddDays(-1),
            LastSeenUtc = DateTime.UtcNow.AddDays(-1)
        });
        await env.Context.SaveChangesAsync();

        var client = new FakeHubSpotClient(
            getFulfilledDeals: (_, _, _) => new HubSpotDealsPageResult(),
            searchDealsByClosedDateRange: (_, _, _, _) => new HubSpotDealsPageResult
            {
                Deals =
                [
                    new HubSpotDealRecord
                    {
                        ExternalDealId = "fresh-window-deal",
                        SaljId = "2875",
                        OwnerEmail = "2875@stl.nu",
                        IsFulfilled = true,
                        FulfilledDateUtc = DateTime.UtcNow.Date,
                        LastModifiedUtc = DateTime.UtcNow,
                        ContactKundstatus = "Nyregistrerad",
                        PayloadHash = "fresh-window"
                    }
                ]
            });

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);

        var remainingDeals = await env.Context.HubSpotDealImports
            .OrderBy(d => d.ExternalDealId)
            .Select(d => d.ExternalDealId)
            .ToListAsync();

        Assert.Equal(new[] { "fresh-window-deal" }, remainingDeals);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_RecalculatesContestEntries_GroupsBySaljId()
    {
        using var env = TestIdentityEnvironment.Create();
        var mappedUser = await env.CreateUserAsync("1234", "1234@stl.nu");

        var contest = new Contest
        {
            Name = "HubSpot Leaderboard Contest",
            StartDate = DateTime.Now.AddDays(-1),
            EndDate = DateTime.Now.AddDays(1),
            IsActive = true,
            CreatedDate = DateTime.Now
        };

        env.Context.Contests.Add(contest);
        await env.Context.SaveChangesAsync();

        var fulfilledAt = DateTime.UtcNow.AddMinutes(-15);
        var client = new FakeHubSpotClient(
            getFulfilledDeals: (_, _, _) => new HubSpotDealsPageResult(),
            searchDealsByClosedDateRange: (_, _, _, _) => new HubSpotDealsPageResult
            {
                Deals =
                [
                    new HubSpotDealRecord
                    {
                        ExternalDealId = "deal-dedupe-1",
                        OwnerId = "owner-dup",
                        OwnerEmail = "owner@stl.nu",
                        SaljId = "1234",
                        FulfilledDateUtc = fulfilledAt,
                        LastModifiedUtc = fulfilledAt,
                        ContactKundstatus = "Klar kund",
                        PayloadHash = "dedupe-1"
                    },
                    new HubSpotDealRecord
                    {
                        ExternalDealId = "deal-dedupe-2",
                        OwnerId = "owner-dup",
                        OwnerEmail = "owner.alias@stl.nu",
                        SaljId = " 1234 ",
                        FulfilledDateUtc = fulfilledAt,
                        LastModifiedUtc = fulfilledAt,
                        ContactKundstatus = "Klar kund",
                        PayloadHash = "dedupe-2"
                    }
                ]
            });

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);

        var entries = await env.Context.ContestEntries
            .Where(e => e.ContestId == contest.Id)
            .ToListAsync();

        Assert.Single(entries);
        Assert.Equal(2, entries[0].DealsCount);
        Assert.Equal("1234", entries[0].EmployeeNumber);
        Assert.Equal(mappedUser.Id, entries[0].UserId);
    }

    private static HubSpotSyncService CreateSut(
        TestIdentityEnvironment env,
        IHubSpotClient client,
        Action<HubSpotOptions>? configure = null)
    {
        var hubSpotOptions = new HubSpotOptions
        {
            Enabled = true,
            MaxPagesPerRun = 1,
            PageSize = 100,
            IncrementalCatchUpLookbackDays = 45,
            UsernameEmailDomain = "stl.nu"
        };
        configure?.Invoke(hubSpotOptions);

        var options = Options.Create(hubSpotOptions);

        var mappingService = new HubSpotMappingService(options);

        return new HubSpotSyncService(
            env.Context,
            client,
            mappingService,
            env.UserManager,
            options,
            NullLogger<HubSpotSyncService>.Instance);
    }
}
