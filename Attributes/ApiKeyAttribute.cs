using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WebApplication2.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAttribute : Attribute, IAuthorizationFilter
{
    private const string ApiKeyHeader = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        // Allow logged-in users (e.g. the browser calculator UI)
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            return;
        }

        // Allow external callers with a valid API key (e.g. Flask)
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedKey = config["ApiKey"];

        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status500InternalServerError);
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey)
            || providedKey != expectedKey)
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
