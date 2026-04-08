namespace WebApplication2.Services.Pricing;

public class PurchasePricingRequest
{
    public decimal CustomerAge { get; set; }
    public decimal BjudAmount { get; set; }
    public decimal? InstallationCost { get; set; }
    public string FinanceOption { get; set; } = "STL-Faktura";

    // Product quantities keyed by product name, e.g. "Startpaket", "Kamera", "Rökdetektor".
    public Dictionary<string, decimal> Quantities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
