using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplication2.Data;
using WebApplication2.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace WebApplication2.Controllers
{
    public class HomeController : Controller
    {
        private const string RememberedEmployeeNumberCookieName = "RememberedEmployeeNumber";
        private readonly STLForumContext _context;
        private readonly ILogger<HomeController> _logger;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager; // Added for role management

        public HomeController(STLForumContext context, ILogger<HomeController> logger, 
                            UserManager<IdentityUser> userManager, SignInManager<IdentityUser> signInManager,
                            RoleManager<IdentityRole> roleManager) // Added RoleManager
        {
            _context = context;
            _logger = logger;
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
        }

        public IActionResult Index()
        {
            return RedirectToAction("Login");
        }

        public IActionResult Login()
        {
            var rememberedEmployeeNumber = Request.Cookies[RememberedEmployeeNumberCookieName];
            ViewBag.RememberedEmployeeNumber = rememberedEmployeeNumber ?? string.Empty;
            ViewBag.RememberMeChecked = !string.IsNullOrWhiteSpace(rememberedEmployeeNumber);
            return View();
        }

        [HttpGet]
        public IActionResult AccessDenied()
        {
            return View();
        }

        [HttpPost]
        [EnableRateLimiting("LoginProtection")]
        public async Task<IActionResult> ProcessLogin(string employeeNumber, string password, bool rememberMe = false)
        {
            try
            {
                employeeNumber = employeeNumber?.Trim() ?? string.Empty;
                var user = await _userManager.FindByNameAsync(employeeNumber);

                if (user != null)
                {
                    var result = await _signInManager.PasswordSignInAsync(user, password, rememberMe, lockoutOnFailure: true);

                    if (result.Succeeded)
                    {
                        if (rememberMe && !string.IsNullOrWhiteSpace(employeeNumber))
                        {
                            Response.Cookies.Append(
                                RememberedEmployeeNumberCookieName,
                                employeeNumber,
                                new CookieOptions
                                {
                                    Expires = DateTimeOffset.UtcNow.AddDays(30),
                                    HttpOnly = true,
                                    IsEssential = true,
                                    Secure = Request.IsHttps,
                                    SameSite = SameSiteMode.Strict
                                });
                        }
                        else
                        {
                            Response.Cookies.Delete(RememberedEmployeeNumberCookieName);
                        }

                        _logger.LogInformation($"User {employeeNumber} logged in successfully");
                
                        var isAdmin = await _userManager.IsInRoleAsync(user, "SuperAdmin");
                
                        if (isAdmin)
                        {
                            return RedirectToAction("Index", "Social");
                        }
                        else
                        {
                            return RedirectToAction("Index", "Social");
                        }
                    }
                    else if (result.IsLockedOut)
                    {
                        _logger.LogWarning($"User {employeeNumber} account locked out");
                        ViewBag.ErrorMessage = "Kontot är låst på grund av för många misslyckade försök.";
                        ViewBag.RememberedEmployeeNumber = employeeNumber;
                        ViewBag.RememberMeChecked = rememberMe;
                        return View("Login");
                    }
                    else
                    {
                        _logger.LogWarning($"Failed login attempt for user {employeeNumber}");
                        ViewBag.ErrorMessage = "Felaktigt anställningsnummer eller lösenord.";
                        ViewBag.RememberedEmployeeNumber = employeeNumber;
                        ViewBag.RememberMeChecked = rememberMe;
                        return View("Login");
                    }
                }
                else
                {
                    _logger.LogWarning($"Login attempt for non-existent user {employeeNumber}");
                    ViewBag.ErrorMessage = "Felaktigt anställningsnummer eller lösenord.";
                    ViewBag.RememberedEmployeeNumber = employeeNumber;
                    ViewBag.RememberMeChecked = rememberMe;
                    return View("Login");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during login");
                ViewBag.ErrorMessage = "Ett fel uppstod vid inloggning. Försök igen.";
                ViewBag.RememberedEmployeeNumber = employeeNumber;
                ViewBag.RememberMeChecked = rememberMe;
                return View("Login");
            }
        }

        public IActionResult Social()
        {
            if (!User.Identity?.IsAuthenticated ?? true)
            {
                return RedirectToAction("Login");
            }

            return RedirectToAction("Index", "Social");
        }

        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> Admin()
        {
            // Get all users with their roles
            var users = await _userManager.Users.ToListAsync();
            
            // Create a list to hold user info with roles
            var usersWithRoles = new List<(IdentityUser User, bool IsAdmin)>();
            
            foreach (var user in users)
            {
                var isAdmin = await _userManager.IsInRoleAsync(user, "SuperAdmin");
                usersWithRoles.Add((user, isAdmin));
            }
            
            ViewBag.UsersWithRoles = usersWithRoles;
            return View(users);
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddUser(string employeeNumber, bool makeAdmin = false)
        {
            try
            {
                // Check if employee number already exists
                var existingUser = await _userManager.FindByNameAsync(employeeNumber);

                if (existingUser != null)
                {
                    TempData["ErrorMessage"] = $"Anställningsnummer {employeeNumber} finns redan.";
                    return RedirectToAction("Admin");
                }

                // Auto-generate a secure password
                var password = GenerateSecurePassword();

                // Create new user using Identity
                var newUser = new IdentityUser
                {
                    UserName = employeeNumber,
                    Email = $"{employeeNumber}@company.com"
                };

                var result = await _userManager.CreateAsync(newUser, password);

                if (result.Succeeded)
                {
                    // If makeAdmin is checked, add to SuperAdmin role
                    if (makeAdmin)
                    {
                        // Ensure SuperAdmin role exists
                        if (!await _roleManager.RoleExistsAsync("SuperAdmin"))
                        {
                            await _roleManager.CreateAsync(new IdentityRole("SuperAdmin"));
                        }
                        
                        await _userManager.AddToRoleAsync(newUser, "SuperAdmin");
                        _logger.LogInformation($"New admin user created: {employeeNumber} by {User.Identity.Name}");
                        TempData["SuccessMessage"] = $"Admin-användare {employeeNumber} har skapats med lösenord: {password}";
                    }
                    else
                    {
                        TempData["SuccessMessage"] = $"Användare {employeeNumber} har skapats med lösenord: {password}";
                    }
                }
                else
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    TempData["ErrorMessage"] = $"Fel vid skapande av användare: {errors}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding user");
                TempData["ErrorMessage"] = "Ett fel uppstod vid skapandet av användare.";
            }

            return RedirectToAction("Admin");
        }

        private string GenerateSecurePassword()
        {
            const string upperCase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string lowerCase = "abcdefghijklmnopqrstuvwxyz";
            const string digits = "0123456789";
            const string special = "!@#$%^&*";
            const string allChars = upperCase + lowerCase + digits + special;
            
            var random = new Random();
            var password = new char[12];
            
            // Ensure at least one of each required character type
            password[0] = upperCase[random.Next(upperCase.Length)];
            password[1] = lowerCase[random.Next(lowerCase.Length)];
            password[2] = digits[random.Next(digits.Length)];
            password[3] = special[random.Next(special.Length)];
            
            // Fill the rest with random characters
            for (int i = 4; i < password.Length; i++)
            {
                password[i] = allChars[random.Next(allChars.Length)];
            }
            
            // Shuffle the password
            return new string(password.OrderBy(x => random.Next()).ToArray());
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleAdminRole(string userId)
        {
            try
            {
                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    TempData["ErrorMessage"] = "Användaren hittades inte.";
                    return RedirectToAction("Admin");
                }

                // Don't allow removing admin role from yourself
                var currentUserId = _userManager.GetUserId(User);
                if (user.Id == currentUserId)
                {
                    TempData["ErrorMessage"] = "Du kan inte ändra din egen admin-status.";
                    return RedirectToAction("Admin");
                }

                var isCurrentlyAdmin = await _userManager.IsInRoleAsync(user, "SuperAdmin");
                
                if (isCurrentlyAdmin)
                {
                    // Remove admin role
                    await _userManager.RemoveFromRoleAsync(user, "SuperAdmin");
                    _logger.LogInformation($"Admin role removed from {user.UserName} by {User.Identity.Name}");
                    TempData["SuccessMessage"] = $"{user.UserName} är inte längre admin.";
                }
                else
                {
                    // Add admin role
                    await _userManager.AddToRoleAsync(user, "SuperAdmin");
                    _logger.LogInformation($"Admin role added to {user.UserName} by {User.Identity.Name}");
                    TempData["SuccessMessage"] = $"{user.UserName} är nu admin.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling admin role");
                TempData["ErrorMessage"] = "Ett fel uppstod vid ändring av admin-status.";
            }

            return RedirectToAction("Admin");
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string userId)
        {
            try
            {
                var user = await _userManager.FindByIdAsync(userId);

                if (user != null)
                {
                    // Don't allow deleting yourself
                    var currentUserId = _userManager.GetUserId(User);
                    if (user.Id == currentUserId)
                    {
                        TempData["ErrorMessage"] = "Du kan inte ta bort dig själv.";
                        return RedirectToAction("Admin");
                    }

                    var result = await _userManager.DeleteAsync(user);

                    if (result.Succeeded)
                    {
                        TempData["SuccessMessage"] = $"Användare {user.UserName} har tagits bort.";
                    }
                    else
                    {
                        var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                        TempData["ErrorMessage"] = $"Fel vid borttagning: {errors}";
                    }
                }
                else
                {
                    TempData["ErrorMessage"] = "Användare hittades inte.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting user");
                TempData["ErrorMessage"] = "Ett fel uppstod vid borttagning av användare.";
            }

            return RedirectToAction("Admin");
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetUserPassword(string userId, string newPassword)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
                {
                    TempData["ErrorMessage"] = "Lösenordet måste vara minst 6 tecken långt.";
                    return RedirectToAction("Admin");
                }

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    TempData["ErrorMessage"] = "Användaren hittades inte.";
                    return RedirectToAction("Admin");
                }

                var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
                var result = await _userManager.ResetPasswordAsync(user, resetToken, newPassword);

                if (result.Succeeded)
                {
                    _logger.LogWarning($"Password reset by admin for user {user.UserName} by {User.Identity.Name}");
                    TempData["SuccessMessage"] = $"Lösenordet för {user.UserName} har återställts.";
                }
                else
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    TempData["ErrorMessage"] = $"Fel vid lösenordsåterställning: {errors}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting user password");
                TempData["ErrorMessage"] = "Ett fel uppstod vid lösenordsåterställning.";
            }

            return RedirectToAction("Admin");
        }

        [HttpGet]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> SearchUsers(string searchTerm)
        {
            List<IdentityUser> users;

            if (!string.IsNullOrEmpty(searchTerm))
            {
                users = await _userManager.Users
                    .Where(u => u.UserName.Contains(searchTerm))
                    .ToListAsync();
                ViewBag.SearchTerm = searchTerm;
            }
            else
            {
                users = await _userManager.Users.ToListAsync();
            }

            // Get roles for display
            var usersWithRoles = new List<(IdentityUser User, bool IsAdmin)>();
            foreach (var user in users)
            {
                var isAdmin = await _userManager.IsInRoleAsync(user, "SuperAdmin");
                usersWithRoles.Add((user, isAdmin));
            }
            
            ViewBag.UsersWithRoles = usersWithRoles;
            return View("Admin", users);
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Profile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction("Login");
            }

            var roles = await _userManager.GetRolesAsync(user);
            var ownerMapping = await _context.HubSpotOwnerMappings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.OwnerUserId == user.Id);

            var monthStartUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEndUtc = monthStartUtc.AddMonths(1);

            var currentMonthDeals = await _context.HubSpotDealImports
                .AsNoTracking()
                .Where(d =>
                    d.OwnerUserId == user.Id &&
                    d.FulfilledDateUtc >= monthStartUtc &&
                    d.FulfilledDateUtc < monthEndUtc)
                .OrderByDescending(d => d.FulfilledDateUtc)
                .Select(d => new UserHubSpotDealViewModel
                {
                    ExternalDealId = d.ExternalDealId,
                    DealName = d.DealName ?? string.Empty,
                    FulfilledDateUtc = d.FulfilledDateUtc,
                    Amount = d.Amount,
                    SellerProvision = d.SellerProvision,
                    CurrencyCode = d.CurrencyCode ?? string.Empty
                })
                .ToListAsync();

            var model = new UserProfileViewModel
            {
                UserId = user.Id,
                Username = user.UserName ?? "Unknown",
                Email = user.Email ?? string.Empty,
                EmailConfirmed = user.EmailConfirmed,
                TwoFactorEnabled = user.TwoFactorEnabled,
                Roles = roles.ToList(),
                QuestionsCount = await _context.Questions.CountAsync(q => q.UserId == user.Id),
                AnswersCount = await _context.Answers.CountAsync(a => a.UserId == user.Id),
                LikesGivenCount = await _context.Likes.CountAsync(l => l.UserId == user.Id),
                ContestEntriesCount = await _context.ContestEntries.CountAsync(ce => ce.UserId == user.Id),
                HubSpotOwnerId = ownerMapping?.HubSpotOwnerId,
                HubSpotOwnerEmail = ownerMapping?.HubSpotOwnerEmail,
                HubSpotOwnerDisplayName = $"{ownerMapping?.HubSpotFirstName} {ownerMapping?.HubSpotLastName}".Trim(),
                HubSpotOwnerArchived = ownerMapping?.IsArchived,
                CurrentMonthDeals = currentMonthDeals,
                CurrentMonthFulfilledDealsCount = currentMonthDeals.Count,
                CurrentMonthFulfilledDealsAmount = currentMonthDeals.Sum(d => d.Amount ?? 0m),
                CurrentMonthFulfilledDealsProvision = currentMonthDeals.Sum(d => d.SellerProvision ?? 0m)
            };

            return View(model);
        }

        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Login");
        }
    }
}
