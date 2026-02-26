using Microsoft.EntityFrameworkCore;
using WebApplication2.Data;
using Microsoft.AspNetCore.Identity;
using WebApplication2.Middlewares;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using WebApplication2.Services;
using WebApplication2.Services.Pricing;
using WebApplication2.Services.HubSpot;

LoadDotEnvIfPresent();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container with CSRF protection (Chapter 6 security requirement)
builder.Services.AddControllersWithViews(options =>
{
    // Global anti-forgery token validation for all POST requests
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
});

// Configure Entity Framework with Identity
builder.Services.AddDbContext<STLForumContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        // Password requirements
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 6;
    
        // Account lockout settings
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    
        // User settings - TILLÅT MELLANSLAG
        options.User.RequireUniqueEmail = false;
        options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+åäöÅÄÖ ";
    })
.AddEntityFrameworkStores<STLForumContext>()
.AddDefaultTokenProviders();

// Register Email Service for question notifications
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IPurchasePricingService, PurchasePricingService>();

// HubSpot integration services (disabled until configured with secrets)
builder.Services.AddOptions<HubSpotOptions>()
    .Bind(builder.Configuration.GetSection("HubSpot"))
    .ValidateDataAnnotations()
        .Validate(
            options =>
                !string.IsNullOrWhiteSpace(options.FulfilledProperty) &&
                (
                    (options.FulfilledValues != null && options.FulfilledValues.Any(v => !string.IsNullOrWhiteSpace(v))) ||
                    !string.IsNullOrWhiteSpace(options.FulfilledValue)
                ) &&
                !string.IsNullOrWhiteSpace(options.DealNameProperty) &&
                !string.IsNullOrWhiteSpace(options.OwnerEmailProperty) &&
                !string.IsNullOrWhiteSpace(options.OwnerIdProperty) &&
                !string.IsNullOrWhiteSpace(options.SaljIdProperty) &&
            !string.IsNullOrWhiteSpace(options.FulfilledDateProperty) &&
            !string.IsNullOrWhiteSpace(options.LastModifiedProperty) &&
            !string.IsNullOrWhiteSpace(options.AmountProperty) &&
            !string.IsNullOrWhiteSpace(options.CurrencyCodeProperty) &&
            !string.IsNullOrWhiteSpace(options.ProvisionProperty) &&
            !string.IsNullOrWhiteSpace(options.UsernameEmailDomain),
        "HubSpot schema mapping is incomplete. Configure mapping keys under HubSpot in appsettings.json.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.AccessToken),
        "HubSpot access token is required when HubSpot integration is enabled.")
    .ValidateOnStart();

builder.Services.AddHttpClient<IHubSpotClient, HubSpotClient>((serviceProvider, client) =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<HubSpotOptions>>()
        .Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddScoped<IHubSpotMappingService, HubSpotMappingService>();
builder.Services.AddScoped<IHubSpotSyncService, HubSpotSyncService>();
builder.Services.AddHostedService<HubSpotSyncBackgroundService>();

// Configure cookie authentication (Chapter 6 best practices)
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Home/Login";
    options.LogoutPath = "/Home/Logout";
    options.AccessDeniedPath = "/Home/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    // Enhanced security settings
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Use HTTPS in production
    options.Cookie.SameSite = SameSiteMode.Strict; // CSRF protection
});

// Add session services with enhanced security
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// Add anti-forgery services with enhanced configuration
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__RequestVerificationToken";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// Add Rate Limiting (Chapter 8 security and performance)
builder.Services.AddRateLimiter(options =>
{
    // Simple fixed window limiter for login protection
    options.AddPolicy("LoginProtection", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5, // 5 login attempts
                Window = TimeSpan.FromMinutes(15), // per 15 minutes
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 2
            }));

    // Simple sliding window for content creation
    options.AddPolicy("ContentCreation", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 10, // 10 posts
                Window = TimeSpan.FromMinutes(10), // per 10 minutes
                SegmentsPerWindow = 2,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 3
            }));

    // Global rate limiter
    options.AddPolicy("GeneralApi", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100, // 100 requests
                Window = TimeSpan.FromMinutes(1), // per minute
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 5
            }));

    // What to do when rate limit is exceeded
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = 429; // Too Many Requests
        context.HttpContext.Response.Headers.Add("Retry-After", "60");
        
        await context.HttpContext.Response.WriteAsync(
            "För många förfrågningar. Försök igen om 60 sekunder.", 
            cancellationToken);
    };
});

// Configure logging (Chapter 7 & 8 recommendations)
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add structured logging in production
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddEventSourceLogger();
}

var app = builder.Build();

// Configure the HTTP request pipeline with proper middleware order (Chapter 8)

// Global error handling must be first to catch all exceptions
app.UseGlobalErrorHandling();

// Request logging for security auditing and performance monitoring (Chapter 8)
app.UseRequestLogging();

// Rate limiting middleware (Chapter 8 - prevents DoS and brute force attacks)
app.UseRateLimiter();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days (Chapter 6 security requirement)
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

// Security: Enforce HTTPS (Chapter 6 requirement)
app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

// IMPORTANT: Authentication must come before Authorization
app.UseAuthentication();
app.UseAuthorization();

app.UseSession();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Auto-create SuperAdmin role and user on startup with secure configuration
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<STLForumContext>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        var autoMigrateOnStartup = configuration.GetValue<bool?>("Database:AutoMigrateOnStartup")
                                  ?? app.Environment.IsDevelopment();

        if (autoMigrateOnStartup)
        {
            const int maxAttempts = 20;
            var attempt = 0;
            Exception? lastException = null;

            while (attempt < maxAttempts)
            {
                attempt++;
                try
                {
                    await dbContext.Database.MigrateAsync();
                    logger.LogInformation("Database migrations applied successfully.");
                    lastException = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    logger.LogWarning(ex, "Database migration attempt {Attempt}/{MaxAttempts} failed. Retrying in 3 seconds...", attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }

            if (lastException != null)
            {
                throw new InvalidOperationException("Failed to apply database migrations on startup.", lastException);
            }
        }

        // Create SuperAdmin role if it doesn't exist
        if (!await roleManager.RoleExistsAsync("SuperAdmin"))
        {
            await roleManager.CreateAsync(new IdentityRole("SuperAdmin"));
            logger.LogInformation("SuperAdmin role created successfully");
        }
        
        // Skapa rollen "Kommenterare" om den inte finns
        if (!await roleManager.RoleExistsAsync("Kommenterare"))
        {
            await roleManager.CreateAsync(new IdentityRole("Kommenterare"));
            logger.LogInformation("Kommenterare-rollen skapades.");
        }

        
        // Get admin credentials from configuration (user secrets/environment variables)
        var adminUsername = configuration["Security:DefaultAdminUsername"] ?? "admin";
        var adminPassword = configuration["Security:DefaultAdminPassword"];
        var adminEmail = configuration["Security:DefaultAdminEmail"] ?? "admin@company.com";
        
        // Validate configuration
        if (string.IsNullOrEmpty(adminPassword))
        {
            logger.LogWarning("No default admin password configured. Admin user creation skipped.");
            logger.LogWarning("To create admin user, configure 'Security:DefaultAdminPassword' in user secrets or environment variables.");
        }
        else
        {
            // Create or update admin user
            var adminUser = await userManager.FindByNameAsync(adminUsername);
            
            if (adminUser == null)
            {
                // Create new admin user
                adminUser = new IdentityUser
                {
                    UserName = adminUsername,
                    Email = adminEmail,
                    EmailConfirmed = true
                };
                
                var result = await userManager.CreateAsync(adminUser, adminPassword);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(adminUser, "SuperAdmin");
                    logger.LogInformation($"Admin user '{adminUsername}' created successfully");
                }
                else
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    logger.LogError($"Failed to create admin user: {errors}");
                }
            }
            else
            {
                // Admin user exists - ensure they're in SuperAdmin role
                if (!await userManager.IsInRoleAsync(adminUser, "SuperAdmin"))
                {
                    await userManager.AddToRoleAsync(adminUser, "SuperAdmin");
                    logger.LogInformation($"Added SuperAdmin role to existing user '{adminUsername}'");
                }
                
                logger.LogInformation($"Admin user '{adminUsername}' already exists and is configured");
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error during admin user initialization");
        throw; // Re-throw to prevent application startup if critical error
    }
}

if (args.Any(a => a.Equals("--hubspot-rebuild-current-month", StringComparison.OrdinalIgnoreCase)))
{
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var syncService = scope.ServiceProvider.GetRequiredService<IHubSpotSyncService>();

    logger.LogInformation("Running one-off HubSpot rebuild for current month only.");
    var result = await syncService.RebuildCurrentMonthOnlyAsync();

    if (!result.Succeeded)
    {
        logger.LogError(
            "HubSpot current-month rebuild failed. Message: {Message}",
            result.Message);
        Environment.ExitCode = 1;
    }
    else
    {
        logger.LogInformation(
            "HubSpot current-month rebuild succeeded. Fetched={Fetched}, Imported={Imported}, Updated={Updated}, Skipped={Skipped}",
            result.DealsFetched,
            result.DealsImported,
            result.DealsUpdated,
            result.DealsSkipped);
    }

    return;
}

app.Run();

static void LoadDotEnvIfPresent()
{
    var currentDirectory = Directory.GetCurrentDirectory();
    var envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

    var envFiles = new List<string> { Path.Combine(currentDirectory, ".env") };
    if (string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase))
    {
        envFiles.Add(Path.Combine(currentDirectory, ".env.dev"));
    }

    foreach (var envPath in envFiles)
    {
        if (!File.Exists(envPath))
        {
            continue;
        }

        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].Trim();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = line[(separatorIndex + 1)..].Trim();
            if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                value = value[1..^1];
            }

            // Keep explicit environment variables from host/container as highest priority.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
