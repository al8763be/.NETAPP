using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApplication2.Services.Pricing;

namespace WebApplication2.Controllers;

[ApiController]
[Route("api/pricing")]
[Authorize]
[IgnoreAntiforgeryToken]
public class PricingController : ControllerBase
{
    private readonly IPurchasePricingService _purchasePricingService;

    public PricingController(IPurchasePricingService purchasePricingService)
    {
        _purchasePricingService = purchasePricingService;
    }

    [HttpPost("calculate")]
    public ActionResult<PurchasePricingResult> Calculate([FromBody] PurchasePricingRequest request)
    {
        try
        {
            var result = _purchasePricingService.Calculate(request);
            return Ok(result);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
