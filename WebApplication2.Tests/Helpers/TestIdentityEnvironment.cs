using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApplication2.Data;

namespace WebApplication2.Tests.Helpers;

public sealed class TestIdentityEnvironment : IDisposable
{
    private TestIdentityEnvironment(ServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
    }

    public ServiceProvider ServiceProvider { get; }

    public STLForumContext Context => ServiceProvider.GetRequiredService<STLForumContext>();

    public UserManager<IdentityUser> UserManager => ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

    public RoleManager<IdentityRole> RoleManager => ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    public SignInManager<IdentityUser> SignInManager => ServiceProvider.GetRequiredService<SignInManager<IdentityUser>>();

    public static TestIdentityEnvironment Create(string? databaseName = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddDbContext<STLForumContext>(options =>
            options.UseInMemoryDatabase(databaseName ?? $"test-db-{Guid.NewGuid():N}"));

        services.AddIdentity<IdentityUser, IdentityRole>(options =>
            {
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 4;
                options.User.RequireUniqueEmail = false;
            })
            .AddEntityFrameworkStores<STLForumContext>()
            .AddDefaultTokenProviders();

        return new TestIdentityEnvironment(services.BuildServiceProvider());
    }

    public async Task<IdentityUser> CreateUserAsync(string username, string? email = null)
    {
        var user = new IdentityUser
        {
            UserName = username,
            Email = email ?? $"{username}@stl.nu",
            EmailConfirmed = true
        };

        var result = await UserManager.CreateAsync(user, "Pass1234!");
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create user {username}: {errors}");
        }

        return user;
    }

    public void Dispose()
    {
        ServiceProvider.Dispose();
    }
}
