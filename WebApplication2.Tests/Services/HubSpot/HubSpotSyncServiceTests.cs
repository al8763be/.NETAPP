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
    public async Task RunIncrementalSyncAsync_SkipsDealWhenOwnerIdIsMissing()
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
    public async Task RunIncrementalSyncAsync_UsesStoredMappingBeforeEmailFallback_AndPersistsOwnerMetadata()
    {
        using var env = TestIdentityEnvironment.Create();
        var mappedUser = await env.CreateUserAsync("1111", "1111@stl.nu");
        await env.CreateUserAsync("2222", "2222@stl.nu");

        env.Context.HubSpotOwnerMappings.Add(new HubSpotOwnerMapping
        {
            HubSpotOwnerId = "owner-1",
            OwnerUserId = mappedUser.Id,
            OwnerUsername = mappedUser.UserName,
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
                            OwnerEmail = "2222@stl.nu",
                            DealName = "Stored mapping precedence",
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 100m,
                            CurrencyCode = "SEK",
                            PayloadHash = "hash-a"
                        }
                    ]
                }
            ],
            owners: new Dictionary<string, HubSpotOwnerRecord?>
            {
                ["owner-1"] = new HubSpotOwnerRecord
                {
                    OwnerId = "owner-1",
                    Email = "2222@stl.nu",
                    FirstName = "Alice",
                    LastName = "Owner",
                    IsArchived = false
                }
            });

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);
        Assert.Equal(0, result.DealsSkipped);

        var deal = await env.Context.HubSpotDealImports.SingleAsync();
        Assert.Equal(mappedUser.Id, deal.OwnerUserId);

        var mapping = await env.Context.HubSpotOwnerMappings.SingleAsync(m => m.HubSpotOwnerId == "owner-1");
        Assert.Equal(mappedUser.Id, mapping.OwnerUserId);
        Assert.Equal("1111", mapping.OwnerUsername);
        Assert.Equal("2222@stl.nu", mapping.HubSpotOwnerEmail);
        Assert.Equal("Alice", mapping.HubSpotFirstName);
        Assert.Equal("Owner", mapping.HubSpotLastName);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_ResolvesByEmailFallback_AndCreatesOwnerMapping()
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
                            DealName = "Email fallback",
                            FulfilledDateUtc = DateTime.UtcNow,
                            LastModifiedUtc = DateTime.UtcNow,
                            Amount = 250m,
                            CurrencyCode = "SEK",
                            PayloadHash = "hash-b"
                        }
                    ]
                }
            ],
            owners: new Dictionary<string, HubSpotOwnerRecord?>
            {
                ["owner-2"] = new HubSpotOwnerRecord
                {
                    OwnerId = "owner-2",
                    Email = "3333@stl.nu",
                    FirstName = "Bob",
                    LastName = "Fallback",
                    IsArchived = false
                }
            });

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DealsImported);
        Assert.Equal(0, result.DealsSkipped);

        var deal = await env.Context.HubSpotDealImports.SingleAsync(d => d.ExternalDealId == "deal-2");
        Assert.Equal(user.Id, deal.OwnerUserId);
        Assert.Equal("3333@stl.nu", deal.OwnerEmail);

        var mapping = await env.Context.HubSpotOwnerMappings.SingleAsync(m => m.HubSpotOwnerId == "owner-2");
        Assert.Equal(user.Id, mapping.OwnerUserId);
        Assert.Equal("3333", mapping.OwnerUsername);
        Assert.Equal("Bob", mapping.HubSpotFirstName);
        Assert.Equal("Fallback", mapping.HubSpotLastName);
    }

    [Fact]
    public async Task RunIncrementalSyncAsync_ImportsDealEvenWhenOwnerCannotBeResolved()
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
            ],
            owners: new Dictionary<string, HubSpotOwnerRecord?>
            {
                ["owner-3"] = new HubSpotOwnerRecord
                {
                    OwnerId = "owner-3",
                    Email = "9999@stl.nu",
                    FirstName = "No",
                    LastName = "Match",
                    IsArchived = false
                }
            });

        var sut = CreateSut(env, client);

        var result = await sut.RunIncrementalSyncAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.DealsSkipped);
        Assert.Equal(1, result.DealsImported);

        var importedDeal = await env.Context.HubSpotDealImports.SingleAsync(d => d.ExternalDealId == "deal-3");
        Assert.Null(importedDeal.OwnerUserId);
        Assert.Equal("9999@stl.nu", importedDeal.OwnerEmail);
        Assert.Equal("owner-3", importedDeal.HubSpotOwnerId);

        var mapping = await env.Context.HubSpotOwnerMappings.SingleAsync(m => m.HubSpotOwnerId == "owner-3");
        Assert.Null(mapping.OwnerUserId);
        Assert.Equal("9999@stl.nu", mapping.HubSpotOwnerEmail);
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
            ],
            owners: new Dictionary<string, HubSpotOwnerRecord?>
            {
                ["owner-redacted"] = new HubSpotOwnerRecord
                {
                    OwnerId = "owner-redacted",
                    Email = "7777@stl.nu",
                    FirstName = "Redacted",
                    LastName = "Case",
                    IsArchived = false
                }
            });

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
                            DealName = "Original",
                            FulfilledDateUtc = firstSyncTime,
                            LastModifiedUtc = firstSyncTime,
                            Amount = 100m,
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
                            DealName = "Updated",
                            FulfilledDateUtc = secondSyncTime,
                            LastModifiedUtc = secondSyncTime,
                            Amount = 250m,
                            CurrencyCode = "SEK",
                            DealStage = "closedwon",
                            PayloadHash = "payload-v2"
                        }
                    ]
                }
            ],
            owners: new Dictionary<string, HubSpotOwnerRecord?>
            {
                ["owner-9"] = new HubSpotOwnerRecord
                {
                    OwnerId = "owner-9",
                    Email = "4444@stl.nu",
                    FirstName = "Update",
                    LastName = "Case",
                    IsArchived = false
                }
            });

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
        Assert.Equal("payload-v2", deal.PayloadHash);
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
