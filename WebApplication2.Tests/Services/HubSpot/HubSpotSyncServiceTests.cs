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

        env.Context.HubSpotOwnerMappings.Add(new HubSpotOwnerMapping
        {
            HubSpotOwnerId = "owner-old",
            OwnerUserId = user.Id,
            OwnerUsername = user.UserName,
            LastSeenUtc = DateTime.UtcNow
        });
        env.Context.HubSpotDealImports.Add(new HubSpotDealImport
        {
            ExternalDealId = "old-deal",
            HubSpotOwnerId = "owner-old",
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
        Assert.Empty(await env.Context.HubSpotOwnerMappings.ToListAsync());
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
    public async Task RunIncrementalSyncAsync_ImportsDealWhenOwnerIdIsMissingButSaljIdExists()
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
        Assert.Null(importedDeal.HubSpotOwnerId);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_UsesSaljIdDirectly_AndIgnoresOwnerMappings()
    {
        using var env = TestIdentityEnvironment.Create();
        var saljareUser = await env.CreateUserAsync("1111", "1111@stl.nu");
        var mappedButDifferentUser = await env.CreateUserAsync("2222", "2222@stl.nu");

        env.Context.HubSpotOwnerMappings.Add(new HubSpotOwnerMapping
        {
            HubSpotOwnerId = "owner-1",
            OwnerUserId = mappedButDifferentUser.Id,
            OwnerUsername = mappedButDifferentUser.UserName,
            HubSpotOwnerEmail = "old@stl.nu",
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
                            ExternalDealId = "deal-1",
                            OwnerId = "owner-1",
                            OwnerEmail = "ignored@stl.nu",
                            SaljId = "1111",
                            DealName = "SaljId precedence",
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 100m,
                            CurrencyCode = "SEK",
                            PayloadHash = "hash-a"
                        }
                    ]
                }
            ]);

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);
        Assert.Equal(0, result.DealsSkipped);

        var deal = await env.Context.HubSpotDealImports.SingleAsync();
        Assert.Equal(saljareUser.Id, deal.OwnerUserId);
        Assert.Equal("1111", deal.SaljId);

        var mapping = await env.Context.HubSpotOwnerMappings.SingleAsync(m => m.HubSpotOwnerId == "owner-1");
        Assert.Equal(mappedButDifferentUser.Id, mapping.OwnerUserId);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_ImportsDealWhenSaljIdExists_WithoutEmailFallback()
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
        Assert.Empty(await env.Context.HubSpotOwnerMappings.ToListAsync());
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_SkipsDealWhenNoSaljId_AndDoesNotCreateOwnerMapping()
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
        Assert.Empty(await env.Context.HubSpotOwnerMappings.ToListAsync());
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
    public async Task RunIncrementalSyncAsync_UpsertsExistingDealIdempotently()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("4444", "4444@stl.nu");

        var firstSyncTime = DateTime.UtcNow.AddMinutes(-10);
        var secondSyncTime = DateTime.UtcNow;

        var client = new FakeHubSpotClient(
            dealPages:
            [
                new HubSpotDealsPageResult
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
                            PayloadHash = "payload-v1"
                        }
                    ]
                },
                new HubSpotDealsPageResult
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
                            PayloadHash = "payload-v2"
                        }
                    ]
                }
            ]);

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
            dealPages:
            [
                new HubSpotDealsPageResult
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
                            PayloadHash = "dedupe-2"
                        }
                    ]
                }
            ]);

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

    private static HubSpotSyncService CreateSut(TestIdentityEnvironment env, IHubSpotClient client)
    {
        var options = Options.Create(new HubSpotOptions
        {
            Enabled = true,
            MaxPagesPerRun = 1,
            PageSize = 100,
            UsernameEmailDomain = "stl.nu"
        });

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
