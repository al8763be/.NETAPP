using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WebApplication2.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAttribute : Attribute, IAuthorizationFilter
{
    private const string ApiKeyHeader = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
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
