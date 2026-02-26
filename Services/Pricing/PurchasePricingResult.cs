namespace WebApplication2.Services.Pricing;

public class PurchasePricingResult
{
    public decimal StartPackageUnitPrice { get; set; }
    public decimal StartPackageTotal { get; set; }
    public decimal AdditionalProductsSubtotal { get; set; }
    public decimal InternalCostSubtotal { get; set; }
    public decimal InstallationCost { get; set; }
    public decimal TotalCost { get; set; }

    public decimal ProvisionBase { get; set; }
    public decimal ProvisionAdditionalProducts { get; set; }
    public decimal ProvisionInstallation { get; set; }
    public decimal ProvisionBeforeAdjustments { get; set; }

    public decimal PriceGapBelowCost { get; set; }
    public decimal PriceGapAboveCost { get; set; }
    public decimal AvailableDiscount { get; set; }
    public decimal AppliedDiscount { get; set; }
    public decimal DiscountProvisionAdjustment { get; set; }
    public decimal AboveCostProvisionBonus { get; set; }

    public string FinanceOption { get; set; } = string.Empty;
    public decimal FinanceOptionProvision { get; set; }
    public bool IncludeFinanceOptionProvisionInTotal { get; set; }

    public decimal TotalProvision { get; set; }

    public List<PurchasePricingLineItem> LineItems { get; set; } = [];
}

public class PurchasePricingLineItem
{
    public string Product { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal LineTotal { get; set; }
}
