using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace WebApplication2.Middlewares
{
    /// <summary>
    /// Global error handling middleware following Chapter 8 best practices
    /// Provides centralized exception handling, logging, and user-friendly error responses
    /// </summary>
    public class GlobalErrorHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalErrorHandlingMiddleware> _logger;

        public GlobalErrorHandlingMiddleware(RequestDelegate next, ILogger<GlobalErrorHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred while processing the request. TraceId: {TraceId}, Path: {Path}, Method: {Method}", 
                    context.TraceIdentifier, context.Request.Path, context.Request.Method);

                await HandleExceptionAsync(context, ex);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            context.Response.ContentType = "application/json";

            var response = context.Response;
            var problemDetails = new ProblemDetails();

            // Set trace identifier for debugging (Chapter 8 recommendation)
            var traceId = context.TraceIdentifier;
            problemDetails.Extensions["traceId"] = traceId;

            switch (exception)
            {
                case UnauthorizedAccessException:
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    problemDetails.Status = (int)HttpStatusCode.Unauthorized;
                    problemDetails.Title = "Unauthorized";
                    problemDetails.Detail = "Du har inte behörighet att utföra denna åtgärd.";
                    break;

                case KeyNotFoundException:
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    problemDetails.Status = (int)HttpStatusCode.NotFound;
                    problemDetails.Title = "Not Found";
                    problemDetails.Detail = "Den begärda resursen kunde inte hittas.";
                    break;

                case ArgumentNullException:
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    problemDetails.Status = (int)HttpStatusCode.BadRequest;
                    problemDetails.Title = "Bad Request";
                    problemDetails.Detail = "Obligatorisk data saknas. Kontrollera din data och försök igen.";
                    break;

                case ArgumentException:
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    problemDetails.Status = (int)HttpStatusCode.BadRequest;
                    problemDetails.Title = "Bad Request";
                    problemDetails.Detail = "Ogiltig begäran. Kontrollera din data och försök igen.";
                    break;

                case InvalidOperationException:
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    problemDetails.Status = (int)HttpStatusCode.BadRequest;
                    problemDetails.Title = "Invalid Operation";
                    problemDetails.Detail = "Åtgärden kan inte utföras just nu.";
                    break;

                case TimeoutException:
                    response.StatusCode = (int)HttpStatusCode.RequestTimeout;
                    problemDetails.Status = (int)HttpStatusCode.RequestTimeout;
                    problemDetails.Title = "Request Timeout";
                    problemDetails.Detail = "Begäran tog för lång tid att bearbeta.";
                    break;

                default:
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    problemDetails.Status = (int)HttpStatusCode.InternalServerError;
                    problemDetails.Title = "Internal Server Error";
                    problemDetails.Detail = "Ett oväntat fel uppstod. Vänligen försök igen senare.";
                    break;
            }

            // Add instance path for better debugging
            problemDetails.Instance = context.Request.Path;

            // In development, include more details for debugging
            if (context.RequestServices.GetService<IWebHostEnvironment>()?.IsDevelopment() == true)
            {
                problemDetails.Detail += $" [Debug: {exception.Message}]";
                problemDetails.Extensions["stackTrace"] = exception.StackTrace;
            }

            // Security: Never expose sensitive information in production
            // The book emphasizes preventing information disclosure

            var jsonResponse = JsonSerializer.Serialize(problemDetails, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await context.Response.WriteAsync(jsonResponse);
        }
    }

    /// <summary>
    /// Extension method for easy middleware registration
    /// </summary>
    public static class GlobalErrorHandlingMiddlewareExtensions
    {
        public static IApplicationBuilder UseGlobalErrorHandling(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<GlobalErrorHandlingMiddleware>();
        }
    }
}