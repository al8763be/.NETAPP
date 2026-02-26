namespace WebApplication2.Services.Pricing;

public interface IPurchasePricingService
{
    PurchasePricingResult Calculate(PurchasePricingRequest request);
}
