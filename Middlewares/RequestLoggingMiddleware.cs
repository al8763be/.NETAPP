using System.Diagnostics;
using System.Text;

namespace WebApplication2.Middlewares
{
    /// <summary>
    /// Request logging middleware following Chapter 8 best practices
    /// Provides security auditing, performance monitoring, and request tracking
    /// </summary>
    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Start performance timer (Chapter 8 recommendation)
            var timestamp = Stopwatch.GetTimestamp();
            var startTime = DateTime.UtcNow;

            // Log incoming request (security auditing)
            var requestInfo = await LogRequestAsync(context);

            // Execute the next middleware in the pipeline
            await _next(context);

            // Calculate request duration
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

            // Log response information (performance monitoring)
            LogResponse(context, requestInfo, elapsedMilliseconds, startTime);
        }

        private async Task<RequestInfo> LogRequestAsync(HttpContext context)
        {
            var request = context.Request;
            var requestInfo = new RequestInfo
            {
                TraceId = context.TraceIdentifier,
                Method = request.Method,
                Path = request.Path.Value ?? "",
                QueryString = request.QueryString.Value ?? "",
                UserAgent = request.Headers.UserAgent.ToString(),
                IPAddress = GetClientIPAddress(context),
                UserId = context.User?.Identity?.Name ?? "Anonymous",
                Timestamp = DateTime.UtcNow
            };

            // Log the incoming request (Chapter 8: security and auditing)
            _logger.LogInformation(
                "Incoming Request: {Method} {Path}{QueryString} | User: {UserId} | IP: {IPAddress} | TraceId: {TraceId} | UserAgent: {UserAgent}",
                requestInfo.Method,
                requestInfo.Path,
                requestInfo.QueryString,
                requestInfo.UserId,
                requestInfo.IPAddress,
                requestInfo.TraceId,
                requestInfo.UserAgent);

            // Security monitoring: Log suspicious patterns
            if (IsSuspiciousRequest(requestInfo))
            {
                _logger.LogWarning(
                    "SECURITY: Suspicious request detected | Method: {Method} | Path: {Path} | IP: {IPAddress} | User: {UserId} | TraceId: {TraceId}",
                    requestInfo.Method,
                    requestInfo.Path,
                    requestInfo.IPAddress,
                    requestInfo.UserId,
                    requestInfo.TraceId);
            }

            return requestInfo;
        }

        private void LogResponse(HttpContext context, RequestInfo requestInfo, double elapsedMilliseconds, DateTime startTime)
        {
            var response = context.Response;

            // Log response information (Chapter 8: performance monitoring)
            _logger.LogInformation(
                "Request Completed: {Method} {Path} | Status: {StatusCode} | Duration: {ElapsedMs}ms | User: {UserId} | TraceId: {TraceId}",
                requestInfo.Method,
                requestInfo.Path,
                response.StatusCode,
                Math.Round(elapsedMilliseconds, 2),
                requestInfo.UserId,
                requestInfo.TraceId);

            // Performance monitoring: Log slow requests
            if (elapsedMilliseconds > 5000) // 5 seconds threshold
            {
                _logger.LogWarning(
                    "PERFORMANCE: Slow request detected | Duration: {ElapsedMs}ms | Method: {Method} | Path: {Path} | TraceId: {TraceId}",
                    Math.Round(elapsedMilliseconds, 2),
                    requestInfo.Method,
                    requestInfo.Path,
                    requestInfo.TraceId);
            }

            // Error monitoring: Log failed requests
            if (response.StatusCode >= 400)
            {
                var logLevel = response.StatusCode >= 500 ? LogLevel.Error : LogLevel.Warning;
                _logger.Log(logLevel,
                    "Request Failed: {Method} {Path} | Status: {StatusCode} | Duration: {ElapsedMs}ms | User: {UserId} | IP: {IPAddress} | TraceId: {TraceId}",
                    requestInfo.Method,
                    requestInfo.Path,
                    response.StatusCode,
                    Math.Round(elapsedMilliseconds, 2),
                    requestInfo.UserId,
                    requestInfo.IPAddress,
                    requestInfo.TraceId);
            }
        }

        private string GetClientIPAddress(HttpContext context)
        {
            // Handle reverse proxy scenarios (production deployment)
            var ipAddress = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
            
            if (string.IsNullOrEmpty(ipAddress))
            {
                ipAddress = context.Request.Headers["X-Real-IP"].FirstOrDefault();
            }
            
            if (string.IsNullOrEmpty(ipAddress))
            {
                ipAddress = context.Connection.RemoteIpAddress?.ToString();
            }

            return ipAddress ?? "Unknown";
        }

        private bool IsSuspiciousRequest(RequestInfo requestInfo)
        {
            // Security patterns to detect (Chapter 6 & 8 security practices)
            var suspiciousPatterns = new[]
            {
                // SQL injection attempts
                "union select", "drop table", "delete from", "insert into",
                "exec(", "xp_", "sp_",
                
                // XSS attempts
                "<script", "javascript:", "vbscript:", "onload=", "onerror=",
                
                // Path traversal attempts
                "../", "..\\", "%2e%2e", "%c0%af",
                
                // Command injection
                "|", "&", ";", "$", "`",
                
                // Common attack tools
                "sqlmap", "burp", "nikto", "nmap",
                
                // Suspicious file extensions in path
                ".php", ".asp", ".jsp", ".cgi"
            };

            var fullRequest = $"{requestInfo.Path}{requestInfo.QueryString}".ToLower();
            var userAgent = requestInfo.UserAgent.ToLower();

            foreach (var pattern in suspiciousPatterns)
            {
                if (fullRequest.Contains(pattern) || userAgent.Contains(pattern))
                {
                    return true;
                }
            }

            // Check for excessive request frequency (basic rate limiting detection)
            if (requestInfo.Path.Contains("login") && requestInfo.Method == "POST")
            {
                // This could be enhanced with actual rate limiting logic
                return false; // For now, just log all login attempts as informational
            }

            return false;
        }

        private class RequestInfo
        {
            public string TraceId { get; set; } = string.Empty;
            public string Method { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string QueryString { get; set; } = string.Empty;
            public string UserAgent { get; set; } = string.Empty;
            public string IPAddress { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }
    }

    /// <summary>
    /// Extension method for easy middleware registration (Chapter 8 best practice)
    /// </summary>
    public static class RequestLoggingMiddlewareExtensions
    {
        public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<RequestLoggingMiddleware>();
        }
    }
}