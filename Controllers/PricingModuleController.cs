using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApplication2.Controllers;

[Authorize]
public class PricingModuleController : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        return View();
    }
}
