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
    public async Task Profile_ReturnsCurrentMonthHubSpotAggregationForLoggedInSaljare()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("5555", "5555@stl.nu");
        var otherUser = await env.CreateUserAsync("6666", "6666@stl.nu");

        var monthStartUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        env.Context.HubSpotDealImports.AddRange(
            new HubSpotDealImport
            {
                ExternalDealId = "m1",
                DealName = "Included one",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                SaljId = "5555",
                IsFulfilled = true,
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
                SaljId = "5555",
                IsFulfilled = true,
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
                SaljId = "5555",
                IsFulfilled = true,
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
                SaljId = "6666",
                IsFulfilled = true,
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
        Assert.Equal("5555", model.Username);

        Assert.Equal(UserProfileViewModel.CurrentMonthOffset, model.SelectedMonthOffset);
        Assert.Equal(2, model.SelectedPeriodFulfilledDealsCount);
        Assert.Equal(125m, model.SelectedPeriodFulfilledDealsAmount);
        Assert.Equal(19.75m, model.SelectedPeriodFulfilledDealsProvision);
        Assert.Equal(2, model.SelectedPeriodDeals.Count);
        Assert.Empty(model.SelectedPeriodLostDeals);
        Assert.Equal(new[] { "m2", "m1" }, model.SelectedPeriodDeals.Select(d => d.ExternalDealId).ToArray());
        Assert.Equal(new decimal?[] { 7.25m, 12.5m }, model.SelectedPeriodDeals.Select(d => d.SellerProvision).ToArray());
    }

    [Fact]
    public async Task Profile_PreviousMonthSelection_ReturnsPreviousMonthFulfilledAndLostDeals()
    {
        using var env = TestIdentityEnvironment.Create();
        var user = await env.CreateUserAsync("5555", "5555@stl.nu");

        var currentMonthStartUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var previousMonthStartUtc = currentMonthStartUtc.AddMonths(-1);

        env.Context.HubSpotDealImports.AddRange(
            new HubSpotDealImport
            {
                ExternalDealId = "prev-fulfilled",
                DealName = "Previous fulfilled",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                SaljId = "5555",
                IsFulfilled = true,
                FulfilledDateUtc = previousMonthStartUtc.AddDays(2),
                Amount = 350m,
                SellerProvision = 35m,
                ContactKundstatus = "Klar kund",
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "prev-lost",
                DealName = "Previous lost",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                SaljId = "5555",
                IsFulfilled = false,
                FulfilledDateUtc = previousMonthStartUtc.AddDays(4),
                Amount = 180m,
                SellerProvision = 18m,
                ContactKundstatus = "Annullerat",
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "prev-winback",
                DealName = "Previous winback",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                SaljId = "5555",
                IsFulfilled = false,
                FulfilledDateUtc = previousMonthStartUtc.AddDays(3),
                Amount = 240m,
                SellerProvision = 24m,
                ContactKundstatus = "winback",
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "prev-saljare",
                DealName = "Previous saljare",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                SaljId = "5555",
                IsFulfilled = false,
                FulfilledDateUtc = previousMonthStartUtc.AddDays(1),
                Amount = 260m,
                SellerProvision = 26m,
                ContactKundstatus = "Säljare",
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "prev-annullerat",
                DealName = "Previous annullerat",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                SaljId = "5555",
                IsFulfilled = false,
                FulfilledDateUtc = previousMonthStartUtc.AddDays(6),
                Amount = 280m,
                SellerProvision = 28m,
                ContactKundstatus = "Annullerat",
                CurrencyCode = "SEK",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            },
            new HubSpotDealImport
            {
                ExternalDealId = "current-lost",
                DealName = "Current lost",
                OwnerEmail = "5555@stl.nu",
                OwnerUserId = user.Id,
                SaljId = "5555",
                IsFulfilled = false,
                FulfilledDateUtc = currentMonthStartUtc.AddDays(1),
                Amount = 210m,
                SellerProvision = 21m,
                ContactKundstatus = "Annullerat",
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

        var actionResult = await controller.Profile(UserProfileViewModel.PreviousMonthOffset);

        var viewResult = Assert.IsType<ViewResult>(actionResult);
        var model = Assert.IsType<UserProfileViewModel>(viewResult.Model);

        Assert.Equal(UserProfileViewModel.PreviousMonthOffset, model.SelectedMonthOffset);
        Assert.Equal(1, model.SelectedPeriodFulfilledDealsCount);
        Assert.Equal(350m, model.SelectedPeriodFulfilledDealsAmount);
        Assert.Equal(35m, model.SelectedPeriodFulfilledDealsProvision);
        Assert.Equal(new[] { "prev-fulfilled" }, model.SelectedPeriodDeals.Select(d => d.ExternalDealId).ToArray());
        Assert.Equal(new[] { "prev-annullerat", "prev-lost", "prev-winback", "prev-saljare" }, model.SelectedPeriodLostDeals.Select(d => d.ExternalDealId).ToArray());
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
