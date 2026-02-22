using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WebApplication2.Controllers;
using WebApplication2.Models;
using WebApplication2.Tests.Helpers;
using Xunit;

namespace WebApplication2.Tests.Controllers;

public class HomeControllerProfileTests
{
    [Fact]
    public async Task Profile_ReturnsCurrentMonthHubSpotAggregationAndOwnerInfo()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("5555", "5555@stl.nu");
        var otherUser = await env.CreateUserAsync("6666", "6666@stl.nu");

        var monthStartUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        env.Context.HubSpotOwnerMappings.Add(new HubSpotOwnerMapping
        {
            HubSpotOwnerId = "owner-5555",
            HubSpotOwnerEmail = "5555@stl.nu",
            HubSpotFirstName = "Eva",
            HubSpotLastName = "Seller",
            OwnerUserId = user.Id,
            OwnerUsername = user.UserName,
            LastSeenUtc = DateTime.UtcNow,
            IsArchived = false
        });

        env.Context.HubSpotDealImports.AddRange(
            new HubSpotDealImport
            {
                ExternalDealId = "m1",
                DealName = "Included one",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                FulfilledDateUtc = monthStartUtc.AddDays(3),
                Amount = 125m,
                SellerProvision = 12.5m,
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "m2",
                DealName = "Included two",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                FulfilledDateUtc = monthStartUtc.AddDays(5),
                Amount = null,
                SellerProvision = 7.25m,
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "old",
                DealName = "Excluded old month",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                FulfilledDateUtc = monthStartUtc.AddSeconds(-1),
                Amount = 999m,
                SellerProvision = 99m,
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "other",
                DealName = "Excluded other user",
                OwnerEmail = "6666@stl.nu",
                OwnerUserId = otherUser.Id,
                FulfilledDateUtc = monthStartUtc.AddDays(4),
                Amount = 500m,
                SellerProvision = 50m,
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            });

        await env.Context.SaveChangesAsync();

        var controller = new HomeController(
            env.Context,
            NullLogger<HomeController>.Instance,
            env.UserManager,
            env.SignInManager,
            env.RoleManager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(user.Id, user.UserName ?? "5555")
                }
            }
        };

        var actionResult = await controller.Profile();

        var viewResult = Assert.IsType<ViewResult>(actionResult);
        var model = Assert.IsType<UserProfileViewModel>(viewResult.Model);

        Assert.Equal(user.Id, model.UserId);
        Assert.True(model.HasHubSpotOwnerMapping);
        Assert.Equal("owner-5555", model.HubSpotOwnerId);
        Assert.Equal("5555@stl.nu", model.HubSpotOwnerEmail);
        Assert.Equal("Eva Seller", model.HubSpotOwnerDisplayName);

        Assert.Equal(2, model.CurrentMonthFulfilledDealsCount);
        Assert.Equal(125m, model.CurrentMonthFulfilledDealsAmount);
        Assert.Equal(19.75m, model.CurrentMonthFulfilledDealsProvision);
        Assert.Equal(2, model.CurrentMonthDeals.Count);
        Assert.Equal(new[] { "m2", "m1" }, model.CurrentMonthDeals.Select(d => d.ExternalDealId).ToArray());
        Assert.Equal(new decimal?[] { 7.25m, 12.5m }, model.CurrentMonthDeals.Select(d => d.SellerProvision).ToArray());
    }

    private static ClaimsPrincipal BuildPrincipal(string userId, string userName)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, userName)
            ],
            authenticationType: "TestAuth");

        return new ClaimsPrincipal(identity);
    }
}
