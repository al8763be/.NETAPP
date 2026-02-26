using System.Text;

namespace WebApplication2.Services.Pricing;

public sealed class PurchasePricingService : IPurchasePricingService
{
    public const string StartPackageProductName = "Startpaket";

    private const decimal StartPackageUnitPriceUnder70 = 32400m;
    private const decimal StartPackageUnitPriceAge70AndOver = 30990m;

    private const decimal BaseProvision = 2500m;
    private const decimal AdditionalProductsProvisionRate = 0.10m;
    private const decimal InstallationProvisionRate = 0.20m;
    private const decimal DiscountProvisionRate = 0.20m;
    private const decimal AboveCostProvisionRate = 0.20m;

    private static readonly IReadOnlyDictionary<string, decimal> AdditionalProductUnitPrices =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kamera"] = 2200m,
            ["Rökdetektor"] = 2000m,
            ["Magnetkontakt"] = 1000m,
            ["Manöverpanel"] = 1490m,
            ["Vattendetektor"] = 1700m,
            ["Livekamera"] = 2200m,
            ["Yale modul"] = 500m,
            ["Rörelsedetektor"] = 1700m,
            ["Glaskrossdetektor"] = 2000m
        };

    private static readonly IReadOnlyDictionary<string, string> CanonicalNameByToken =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["startpaket"] = StartPackageProductName,
            ["startpackage"] = StartPackageProductName,

            ["kamera"] = "Kamera",

            ["rokdetektor"] = "Rökdetektor",
            ["rokdetector"] = "Rökdetektor",

            ["magnetkontakt"] = "Magnetkontakt",

            ["manoverpanel"] = "Manöverpanel",
            ["manoverpanelen"] = "Manöverpanel",

            ["vattendetektor"] = "Vattendetektor",
            ["waterdetector"] = "Vattendetektor",

            ["livekamera"] = "Livekamera",

            ["yalemodul"] = "Yale modul",
            ["yalemodule"] = "Yale modul",

            ["rorelsedetektor"] = "Rörelsedetektor",
            ["rorelsedetector"] = "Rörelsedetektor",

            ["glaskrossdetektor"] = "Glaskrossdetektor",
            ["glassbreakdetector"] = "Glaskrossdetektor"
        };

    private static readonly IReadOnlyDictionary<string, decimal> FinanceOptionProvisionMap =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["STL-Faktura"] = 0m,
            ["Svea-Faktura"] = 500m,
            ["72Mån"] = 0m,
            ["60Mån"] = 0m,
            ["36Mån"] = 500m,
            ["24Mån"] = 500m,
            ["120MånRänta"] = 500m
        };

    public PurchasePricingResult Calculate(PurchasePricingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CustomerAge < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.CustomerAge), "Age cannot be negative.");
        }

        if (request.FinalPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.FinalPrice), "Final price cannot be negative.");
        }

        if (request.InstallationCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.InstallationCost), "Installation cost cannot be negative.");
        }

        var normalizedQuantities = NormalizeQuantities(request.Quantities);

        var startPackageQuantity = GetQuantity(normalizedQuantities, StartPackageProductName, defaultQuantity: 1m);
        var startPackageUnitPrice = request.CustomerAge >= 70m
            ? StartPackageUnitPriceAge70AndOver
            : StartPackageUnitPriceUnder70;
        var startPackageTotal = startPackageQuantity * startPackageUnitPrice;

        var lineItems = new List<PurchasePricingLineItem>
        {
            new()
            {
                Product = StartPackageProductName,
                UnitPrice = startPackageUnitPrice,
                Quantity = startPackageQuantity,
                LineTotal = startPackageTotal
            }
        };

        decimal additionalProductsSubtotal = 0m;
        foreach (var (productName, unitPrice) in AdditionalProductUnitPrices)
        {
            var quantity = GetQuantity(normalizedQuantities, productName, defaultQuantity: 0m);
            var lineTotal = quantity * unitPrice;
            additionalProductsSubtotal += lineTotal;

            lineItems.Add(new PurchasePricingLineItem
            {
                Product = productName,
                UnitPrice = unitPrice,
                Quantity = quantity,
                LineTotal = lineTotal
            });
        }

        // Mirrors the workbook structure (Kostnader M4/N4/P4):
        // M4 = Startpaket + produkter, N4 = produkter, P4 = M4 + N4 + installation
        var internalCostSubtotal = startPackageTotal + additionalProductsSubtotal;
        var totalCost = internalCostSubtotal + additionalProductsSubtotal + request.InstallationCost;

        var provisionBase = BaseProvision;
        var provisionAdditionalProducts = additionalProductsSubtotal * AdditionalProductsProvisionRate;
        var provisionInstallation = request.InstallationCost * InstallationProvisionRate;
        var provisionBeforeAdjustments = provisionBase + provisionAdditionalProducts + provisionInstallation;

        var priceGapBelowCost = Math.Max(totalCost - request.FinalPrice, 0m);
        var priceGapAboveCost = Math.Max(request.FinalPrice - totalCost, 0m);

        var availableDiscount = GetAvailableDiscount(internalCostSubtotal);
        var appliedDiscount = Math.Min(priceGapBelowCost, availableDiscount);

        // Workbook B16 = -B15*0.2, D11*0.2 added when final price is above total cost.
        var discountProvisionAdjustment = -appliedDiscount * DiscountProvisionRate;
        var aboveCostProvisionBonus = priceGapAboveCost * AboveCostProvisionRate;

        var financeOptionProvision = GetFinanceOptionProvision(request.FinanceOption);

        var totalProvision =
            provisionBeforeAdjustments +
            discountProvisionAdjustment +
            aboveCostProvisionBonus +
            (request.IncludeFinanceOptionProvisionInTotal ? financeOptionProvision : 0m);

        return new PurchasePricingResult
        {
            StartPackageUnitPrice = startPackageUnitPrice,
            StartPackageTotal = startPackageTotal,
            AdditionalProductsSubtotal = additionalProductsSubtotal,
            InternalCostSubtotal = internalCostSubtotal,
            InstallationCost = request.InstallationCost,
            TotalCost = totalCost,
            ProvisionBase = provisionBase,
            ProvisionAdditionalProducts = provisionAdditionalProducts,
            ProvisionInstallation = provisionInstallation,
            ProvisionBeforeAdjustments = provisionBeforeAdjustments,
            PriceGapBelowCost = priceGapBelowCost,
            PriceGapAboveCost = priceGapAboveCost,
            AvailableDiscount = availableDiscount,
            AppliedDiscount = appliedDiscount,
            DiscountProvisionAdjustment = discountProvisionAdjustment,
            AboveCostProvisionBonus = aboveCostProvisionBonus,
            FinanceOption = request.FinanceOption,
            FinanceOptionProvision = financeOptionProvision,
            IncludeFinanceOptionProvisionInTotal = request.IncludeFinanceOptionProvisionInTotal,
            TotalProvision = totalProvision,
            LineItems = lineItems
        };
    }

    private static Dictionary<string, decimal> NormalizeQuantities(IDictionary<string, decimal>? quantities)
    {
        var normalized = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        if (quantities == null)
        {
            return normalized;
        }

        foreach (var (rawKey, rawQuantity) in quantities)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                continue;
            }

            if (rawQuantity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quantities), $"Quantity for '{rawKey}' cannot be negative.");
            }

            var canonicalKey = ToCanonicalProductName(rawKey);
            if (normalized.TryGetValue(canonicalKey, out var existingQuantity))
            {
                normalized[canonicalKey] = existingQuantity + rawQuantity;
                continue;
            }

            normalized[canonicalKey] = rawQuantity;
        }

        return normalized;
    }

    private static string ToCanonicalProductName(string rawKey)
    {
        var token = BuildToken(rawKey);
        if (CanonicalNameByToken.TryGetValue(token, out var canonicalName))
        {
            return canonicalName;
        }

        throw new ArgumentException($"Unknown product '{rawKey}'.", nameof(rawKey));
    }

    private static string BuildToken(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var token = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = char.GetUnicodeCategory(ch);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                token.Append(char.ToLowerInvariant(ch));
            }
        }

        return token.ToString();
    }

    private static decimal GetQuantity(IReadOnlyDictionary<string, decimal> quantities, string productName, decimal defaultQuantity)
    {
        return quantities.TryGetValue(productName, out var quantity)
            ? quantity
            : defaultQuantity;
    }

    // Mirrors workbook bracket logic in Kostnader!H12.
    private static decimal GetAvailableDiscount(decimal internalCostSubtotal)
    {
        if (internalCostSubtotal >= 30990m && internalCostSubtotal <= 37499m)
        {
            return 2200m;
        }

        if (internalCostSubtotal >= 37500m && internalCostSubtotal <= 42499m)
        {
            return 4400m;
        }

        if (internalCostSubtotal >= 42500m && internalCostSubtotal <= 47499m)
        {
            return 6600m;
        }

        if (internalCostSubtotal >= 47500m && internalCostSubtotal <= 52499m)
        {
            return 8500m;
        }

        if (internalCostSubtotal >= 52000m)
        {
            return 10000m;
        }

        return 0m;
    }

    private static decimal GetFinanceOptionProvision(string? financeOption)
    {
        if (string.IsNullOrWhiteSpace(financeOption))
        {
            return 0m;
        }

        if (FinanceOptionProvisionMap.TryGetValue(financeOption.Trim(), out var exactMatchValue))
        {
            return exactMatchValue;
        }

        var token = BuildToken(financeOption);
        foreach (var (optionName, value) in FinanceOptionProvisionMap)
        {
            if (BuildToken(optionName).Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return 0m;
    }
}
