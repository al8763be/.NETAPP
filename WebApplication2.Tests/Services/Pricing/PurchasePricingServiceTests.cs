using WebApplication2.Services.Pricing;
using Xunit;

namespace WebApplication2.Tests.Services.Pricing;

public class PurchasePricingServiceTests
{
    private readonly PurchasePricingService _sut = new();

    [Fact]
    public void Calculate_ReplicatesWorkbookBaselineScenario()
    {
        var request = new PurchasePricingRequest
        {
            CustomerAge = 68,
            BjudAmount = 5980,
            InstallationCost = 0,
            FinanceOption = "STL-Faktura",
            Quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["Startpaket"] = 1,
                ["Kamera"] = 1,
                ["Rökdetektor"] = 1,
                ["Magnetkontakt"] = 1,
                ["Manöverpanel"] = 1,
                ["Vattendetektor"] = 1,
                ["livekamera"] = 1,
                ["Yale modul"] = 1,
                ["rörelsedetektor"] = 1,
                ["Glaskrossdetektor"] = 1
            }
        };

        var result = _sut.Calculate(request);

        Assert.Equal(32400m, result.StartPackageUnitPrice);
        Assert.Equal(32400m, result.StartPackageTotal);
        Assert.Equal(14790m, result.AdditionalProductsSubtotal);
        Assert.Equal(47190m, result.InternalCostSubtotal);
        Assert.Equal(61980m, result.TotalCost);
        Assert.Equal(56000m, result.FinalPrice);

        Assert.Equal(2500m, result.ProvisionBase);
        Assert.Equal(1479m, result.ProvisionAdditionalProducts);
        Assert.Equal(3979m, result.ProvisionBeforeAdjustments);

        Assert.Equal(5980m, result.PriceGapBelowCost);
        Assert.Equal(0m, result.PriceGapAboveCost);
        Assert.Equal(6600m, result.AvailableDiscount);
        Assert.Equal(5980m, result.AppliedDiscount);
        Assert.Equal(-1196m, result.DiscountProvisionAdjustment);

        Assert.Equal(2783m, result.TotalProvision);
    }

    [Fact]
    public void Calculate_UsesSeniorStartPackagePrice_WhenAgeIs70OrMore()
    {
        var request = new PurchasePricingRequest
        {
            CustomerAge = 70,
            BjudAmount = 0,
            InstallationCost = 0,
            FinanceOption = "STL-Faktura",
            Quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["Startpaket"] = 1
            }
        };

        var result = _sut.Calculate(request);

        Assert.Equal(30990m, result.StartPackageUnitPrice);
        Assert.Equal(30990m, result.TotalCost);
        Assert.Equal(30990m, result.FinalPrice);
        Assert.Equal(2500m, result.TotalProvision);
    }

    [Fact]
    public void Calculate_CalculatesFinalPriceFromTotalCostMinusBjud()
    {
        var request = new PurchasePricingRequest
        {
            CustomerAge = 68,
            BjudAmount = 1000,
            InstallationCost = 0,
            FinanceOption = "STL-Faktura",
            Quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["Startpaket"] = 1
            }
        };

        var result = _sut.Calculate(request);

        Assert.Equal(32400m, result.TotalCost);
        Assert.Equal(31400m, result.FinalPrice);
        Assert.Equal(0m, result.PriceGapAboveCost);
        Assert.Equal(0m, result.AboveCostProvisionBonus);
        Assert.Equal(2300m, result.TotalProvision);
    }

    [Fact]
    public void Calculate_CanIncludeFinanceOptionProvision_WhenRequested()
    {
        var request = new PurchasePricingRequest
        {
            CustomerAge = 68,
            BjudAmount = 0,
            InstallationCost = 0,
            FinanceOption = "Svea-Faktura",
            IncludeFinanceOptionProvisionInTotal = true,
            Quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["Startpaket"] = 1
            }
        };

        var result = _sut.Calculate(request);

        Assert.Equal(500m, result.FinanceOptionProvision);
        Assert.Equal(3000m, result.TotalProvision);
    }

    [Fact]
    public void Calculate_Applies30PercentChargeOnBjudExcessOverThreshold()
    {
        var request = new PurchasePricingRequest
        {
            CustomerAge = 68,
            BjudAmount = 2400,
            InstallationCost = 0,
            FinanceOption = "STL-Faktura",
            Quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["Startpaket"] = 1
            }
        };

        var result = _sut.Calculate(request);

        Assert.Equal(2400m, result.BjudAmount);
        Assert.Equal(2200m, result.BjudThreshold);
        Assert.Equal(200m, result.ExcessBjudAmount);
        Assert.Equal(-440m, result.DiscountProvisionAdjustment);
        Assert.Equal(-60m, result.ExcessBjudProvisionAdjustment);
        Assert.Equal(2000m, result.TotalProvision);
    }

    [Fact]
    public void Calculate_AcceptsProductAliasesWithoutDiacritics()
    {
        var request = new PurchasePricingRequest
        {
            CustomerAge = 68,
            BjudAmount = 0,
            Quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["startpackage"] = 1,
                ["rokdetektor"] = 2
            }
        };

        var result = _sut.Calculate(request);

        Assert.Equal(4000m, result.LineItems.Single(i => i.Product == "Rökdetektor").LineTotal);
    }
}
