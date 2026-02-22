using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplication2.Data;
using WebApplication2.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.RateLimiting;
using WebApplication2.Services;
using WebApplication2.Services.HubSpot;

namespace WebApplication2.Controllers
{
    [Authorize]
    [EnableRateLimiting("GeneralApi")] // General protection for all social features
    public class SocialController : Controller
    {
        private readonly STLForumContext _context;
        private readonly ILogger<SocialController> _logger;
        private readonly UserManager<IdentityUser> _userManager;
        private readonly IEmailService _emailService;
        private readonly IHubSpotSyncService _hubSpotSyncService;
        private const string SalesTeamPrefix = "Sälj";

        public SocialController(
            STLForumContext context,
            ILogger<SocialController> logger,
            UserManager<IdentityUser> userManager,
            IEmailService emailService,
            IHubSpotSyncService hubSpotSyncService)
        {
            _context = context;
            _logger = logger;
            _userManager = userManager;
            _emailService = emailService;
            _hubSpotSyncService = hubSpotSyncService;
        }

        // Helper methods
        private async Task<string> GetUsernameByIdAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                return "Okänd användare";
                
            var user = await _userManager.FindByIdAsync(userId);
            return user?.UserName ?? "Okänd användare";
        }

        private async Task<Dictionary<string, string>> GetUserNamesDictionaryAsync(IEnumerable<string> userIds)
        {
            var userNames = new Dictionary<string, string>();
            var distinctUserIds = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct();

            foreach (var userId in distinctUserIds)
            {
                userNames[userId] = await GetUsernameByIdAsync(userId);
            }

            return userNames;
        }

        private void DecodeHtmlEntities(Question question)
        {
            question.Title = System.Net.WebUtility.HtmlDecode(question.Title);
            question.Content = System.Net.WebUtility.HtmlDecode(question.Content);
            
            foreach (var answer in question.Answers)
            {
                answer.Content = System.Net.WebUtility.HtmlDecode(answer.Content);
            }
        }

        private void DecodeHtmlEntities(Contest contest)
        {
            contest.Name = System.Net.WebUtility.HtmlDecode(contest.Name);
            if (!string.IsNullOrEmpty(contest.Description))
            {
                contest.Description = System.Net.WebUtility.HtmlDecode(contest.Description);
            }
        }

        private async Task<Dictionary<int, List<ContestEntry>>> BuildLiveContestLeaderboardsAsync(
            IEnumerable<Contest> contests,
            int topCount,
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<int, List<ContestEntry>>();
            foreach (var contest in contests)
            {
                var entries = await BuildLiveContestEntriesForContestAsync(contest, cancellationToken);
                result[contest.Id] = topCount > 0 ? entries.Take(topCount).ToList() : entries;
            }

            return result;
        }

        private async Task<List<ContestEntry>> BuildLiveContestEntriesForContestAsync(
            Contest contest,
            CancellationToken cancellationToken = default)
        {
            var contestStartUtc = DateTime.SpecifyKind(contest.StartDate.Date, DateTimeKind.Local).ToUniversalTime();
            var contestEndExclusiveUtc = DateTime.SpecifyKind(contest.EndDate.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime();

            var groupedDeals = await _context.HubSpotDealImports
                .AsNoTracking()
                .Where(d =>
                    d.FulfilledDateUtc >= contestStartUtc &&
                    d.FulfilledDateUtc < contestEndExclusiveUtc &&
                    (!string.IsNullOrWhiteSpace(d.HubSpotOwnerId) || !string.IsNullOrWhiteSpace(d.OwnerEmail)))
                .GroupBy(d => new
                {
                    d.HubSpotOwnerId,
                    d.OwnerEmail
                })
                .Select(g => new
                {
                    g.Key.HubSpotOwnerId,
                    g.Key.OwnerEmail,
                    DealsCount = g.Count()
                })
                .ToListAsync(cancellationToken);

            if (!groupedDeals.Any())
            {
                return new List<ContestEntry>();
            }

            var ownerIds = groupedDeals
                .Select(g => NormalizeOwnerId(g.HubSpotOwnerId))
                .Where(id => id != null)
                .Select(id => id!)
                .Distinct()
                .ToList();

            var normalizedOwnerEmails = groupedDeals
                .Select(g => NormalizeOwnerEmail(g.OwnerEmail))
                .Where(email => email != null)
                .Select(email => email!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var mappings = await _context.HubSpotOwnerMappings
                .AsNoTracking()
                .Where(m =>
                    ownerIds.Contains(m.HubSpotOwnerId) ||
                    (m.HubSpotOwnerEmail != null && normalizedOwnerEmails.Contains(m.HubSpotOwnerEmail.ToLower())))
                .ToListAsync(cancellationToken);

            var groupedByOwner = new Dictionary<string, (string? HubSpotOwnerId, string? OwnerEmail, HubSpotOwnerMapping? Mapping, int DealsCount)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var grouped in groupedDeals)
            {
                var mapping = ResolveOwnerMapping(mappings, grouped.HubSpotOwnerId, grouped.OwnerEmail);
                if (!IsSalesPrimaryTeam(mapping))
                {
                    continue;
                }

                var ownerKey = BuildOwnerAggregationKey(grouped.HubSpotOwnerId, grouped.OwnerEmail, mapping);
                if (groupedByOwner.TryGetValue(ownerKey, out var existing))
                {
                    groupedByOwner[ownerKey] = (
                        HubSpotOwnerId: FirstNonEmptyTrim(existing.HubSpotOwnerId, grouped.HubSpotOwnerId, mapping?.HubSpotOwnerId),
                        OwnerEmail: FirstNonEmptyTrim(existing.OwnerEmail, grouped.OwnerEmail, mapping?.HubSpotOwnerEmail),
                        Mapping: existing.Mapping ?? mapping,
                        DealsCount: existing.DealsCount + grouped.DealsCount
                    );
                    continue;
                }

                groupedByOwner[ownerKey] = (
                    HubSpotOwnerId: FirstNonEmptyTrim(grouped.HubSpotOwnerId, mapping?.HubSpotOwnerId),
                    OwnerEmail: FirstNonEmptyTrim(grouped.OwnerEmail, mapping?.HubSpotOwnerEmail),
                    Mapping: mapping,
                    DealsCount: grouped.DealsCount
                );
            }

            var entries = new List<ContestEntry>(groupedByOwner.Count);
            foreach (var groupedOwner in groupedByOwner.Values)
            {
                var displayLabel = BuildContestDisplayLabel(
                    groupedOwner.HubSpotOwnerId,
                    groupedOwner.OwnerEmail,
                    groupedOwner.Mapping);

                if (string.IsNullOrWhiteSpace(displayLabel))
                {
                    displayLabel = "Okänd owner";
                }

                displayLabel = displayLabel.Trim();
                if (displayLabel.Length > 50)
                {
                    displayLabel = displayLabel[..50];
                }

                entries.Add(new ContestEntry
                {
                    ContestId = contest.Id,
                    UserId = groupedOwner.Mapping?.OwnerUserId,
                    EmployeeNumber = displayLabel,
                    DealsCount = groupedOwner.DealsCount,
                    UpdatedDate = DateTime.Now
                });
            }

            return entries
                .OrderByDescending(e => e.DealsCount)
                .ThenBy(e => e.EmployeeNumber)
                .ToList();
        }

        private static bool IsSalesPrimaryTeam(HubSpotOwnerMapping? mapping)
        {
            if (string.IsNullOrWhiteSpace(mapping?.HubSpotPrimaryTeamName))
            {
                return false;
            }

            return mapping.HubSpotPrimaryTeamName
                .TrimStart()
                .StartsWith(SalesTeamPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static HubSpotOwnerMapping? ResolveOwnerMapping(
            IEnumerable<HubSpotOwnerMapping> mappings,
            string? hubSpotOwnerId,
            string? ownerEmail)
        {
            if (!string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                var normalizedOwnerId = NormalizeOwnerId(hubSpotOwnerId);
                var byOwnerId = mappings.FirstOrDefault(m => m.HubSpotOwnerId == normalizedOwnerId);
                if (byOwnerId != null)
                {
                    return byOwnerId;
                }
            }

            if (!string.IsNullOrWhiteSpace(ownerEmail))
            {
                var normalizedEmail = NormalizeOwnerEmail(ownerEmail);
                return mappings.FirstOrDefault(m =>
                    !string.IsNullOrWhiteSpace(m.HubSpotOwnerEmail) &&
                    m.HubSpotOwnerEmail.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static string BuildContestDisplayLabel(
            string? hubSpotOwnerId,
            string? ownerEmail,
            HubSpotOwnerMapping? mapping)
        {
            var teamSuffix = string.Empty;
            if (!string.IsNullOrWhiteSpace(mapping?.HubSpotPrimaryTeamName))
            {
                teamSuffix = $" ({mapping.HubSpotPrimaryTeamName})";
            }

            var mappedName = $"{mapping?.HubSpotFirstName} {mapping?.HubSpotLastName}".Trim();
            if (!string.IsNullOrWhiteSpace(mappedName))
            {
                return $"{mappedName}{teamSuffix}";
            }

            if (!string.IsNullOrWhiteSpace(mapping?.HubSpotOwnerEmail))
            {
                return $"{mapping.HubSpotOwnerEmail.Trim()}{teamSuffix}";
            }

            if (!string.IsNullOrWhiteSpace(ownerEmail))
            {
                return $"{ownerEmail.Trim()}{teamSuffix}";
            }

            if (!string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                return $"HubSpot-{hubSpotOwnerId.Trim()}{teamSuffix}";
            }

            return "Okänd owner";
        }

        private static string BuildOwnerAggregationKey(
            string? hubSpotOwnerId,
            string? ownerEmail,
            HubSpotOwnerMapping? mapping)
        {
            var canonicalOwnerId = FirstNonEmptyTrim(hubSpotOwnerId, mapping?.HubSpotOwnerId);
            if (!string.IsNullOrWhiteSpace(canonicalOwnerId))
            {
                return $"id:{canonicalOwnerId}";
            }

            var normalizedEmail = NormalizeOwnerEmail(FirstNonEmptyTrim(ownerEmail, mapping?.HubSpotOwnerEmail));
            if (!string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return $"email:{normalizedEmail}";
            }

            return $"unknown:{hubSpotOwnerId?.Trim()}:{ownerEmail?.Trim()}";
        }

        private static string? NormalizeOwnerId(string? hubSpotOwnerId)
        {
            if (string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                return null;
            }

            return hubSpotOwnerId.Trim();
        }

        private static string? NormalizeOwnerEmail(string? ownerEmail)
        {
            if (string.IsNullOrWhiteSpace(ownerEmail))
            {
                return null;
            }

            return ownerEmail.Trim().ToLower();
        }

        private static string? FirstNonEmptyTrim(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        public async Task<IActionResult> Index()
        {
            // Get frequently asked questions
            var frequentlyAsked = await _context.Questions
                .Where(q => q.IsFrequentlyAsked)
                .Include(q => q.Answers)
                .OrderByDescending(q => q.Likes)
                .Take(5)
                .ToListAsync();

            // Get recent questions
            var recentQuestions = await _context.Questions
                .Where(q => !q.IsFrequentlyAsked)
                .Include(q => q.Answers)
                .OrderByDescending(q => q.Likes)
                .ThenByDescending(q => q.CreatedDate)
                .Take(10)
                .ToListAsync();

            // Get all user IDs for username lookup
            var allUserIds = frequentlyAsked.Select(q => q.UserId)
                .Concat(recentQuestions.Select(q => q.UserId))
                .Concat(frequentlyAsked.SelectMany(q => q.Answers.Select(a => a.UserId)))
                .Concat(recentQuestions.SelectMany(q => q.Answers.Select(a => a.UserId)))
                .Where(id => !string.IsNullOrEmpty(id));

            var userNames = await GetUserNamesDictionaryAsync(allUserIds);

            // Decode HTML entities
            foreach (var question in frequentlyAsked.Concat(recentQuestions))
            {
                DecodeHtmlEntities(question);
            }

            // Get ongoing contests
            var contests = await _context.Contests
                .Where(c => c.IsActive && c.EndDate > DateTime.Now)
                .OrderBy(c => c.EndDate)
                .Take(3)
                .ToListAsync();
            var contestLeaderboards = await BuildLiveContestLeaderboardsAsync(contests, 3);

            // Decode HTML entities for contests
            foreach (var contest in contests)
            {
                DecodeHtmlEntities(contest);
            }

            ViewBag.FrequentlyAsked = frequentlyAsked;
            ViewBag.RecentQuestions = recentQuestions;
            ViewBag.Contests = contests;
            ViewBag.ContestLeaderboards = contestLeaderboards;
            ViewBag.CurrentUsername = User.Identity?.Name ?? "Unknown";
            ViewBag.UserRole = User.IsInRole("SuperAdmin") ? "SuperAdmin" : "User";
            ViewBag.UserNames = userNames;

            return View();
        }

        [HttpGet]
        public IActionResult CreateQuestion()
        {
            ViewBag.CurrentUsername = User.Identity?.Name ?? "Unknown";
            ViewBag.UserRole = User.IsInRole("SuperAdmin") ? "SuperAdmin" : "User";
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken] // CRITICAL: CSRF protection as per Chapter 6
        [EnableRateLimiting("ContentCreation")] // Prevents spam
        public async Task<IActionResult> CreateQuestion(string title, string content, string category, IFormFile attachment)
        {
            var currentUserId = _userManager.GetUserId(User);
            var currentUsername = User.Identity?.Name ?? "Unknown";
            
            if (string.IsNullOrEmpty(currentUserId))
            {
                return RedirectToAction("Login", "Home");
            }

            // Input validation and sanitization (Chapter 7 best practices)
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            {
                TempData["ErrorMessage"] = "Titel och innehåll måste fyllas i.";
                return RedirectToAction("Index");
            }

            // Sanitize inputs to prevent XSS
            title = SanitizeInput(title);
            content = SanitizeInput(content);

            // Additional validation for dangerous content
            if (ContainsDangerousContent(title) || ContainsDangerousContent(content))
            {
                TempData["ErrorMessage"] = "Innehållet innehåller potentiellt farliga element och kan inte accepteras.";
                return RedirectToAction("Index");
            }

            // Length validation
            if (title.Length > 200 || content.Length > 5000)
            {
                TempData["ErrorMessage"] = "Titel eller innehåll är för långt.";
                return RedirectToAction("Index");
            }

            string attachmentFileName = null;
            string attachmentPath = null;
            
            // Enhanced file upload security (following Chapter 6 recommendations)
            if (attachment != null && attachment.Length > 0)
            {
                var validationResult = await ValidateFileUploadAsync(attachment);
                if (!validationResult.IsValid)
                {
                    TempData["ErrorMessage"] = validationResult.ErrorMessage;
                    return RedirectToAction("Index");
                }
                
                attachmentFileName = SanitizeFileName(attachment.FileName);
                var fileName = $"{Guid.NewGuid()}{Path.GetExtension(attachmentFileName).ToLower()}";
                var uploadsPath = Path.Combine("wwwroot", "uploads");
                
                if (!Directory.Exists(uploadsPath))
                    Directory.CreateDirectory(uploadsPath);
                    
                attachmentPath = Path.Combine("uploads", fileName);
                var fullPath = Path.Combine("wwwroot", attachmentPath);
                
                using (var stream = new FileStream(fullPath, FileMode.Create))
                {
                    await attachment.CopyToAsync(stream);
                }
            }

            try
            {
                var question = new Question
                {
                    Title = title.Trim(),
                    Content = content.Trim(),
                    Category = category ?? "säljfråga",
                    UserId = currentUserId,
                    AttachmentFileName = attachmentFileName,
                    AttachmentPath = attachmentPath,
                    CreatedDate = DateTime.Now
                };

                _context.Questions.Add(question);
                await _context.SaveChangesAsync();

                // Send email notification based on category
                try
                {
                    var questionUrl = $"{Request.Scheme}://{Request.Host}/Social/QuestionDetail/{question.Id}";
                    await _emailService.SendQuestionNotificationAsync(
                        question.Category,
                        question.Title,
                        question.Content,
                        currentUsername,
                        questionUrl
                    );
                }
                catch (Exception emailEx)
                {
                    _logger.LogError(emailEx, "Failed to send email notification for question");
                }

                TempData["SuccessMessage"] = "Din fråga har publicerats!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating question");
                TempData["ErrorMessage"] = "Ett fel uppstod vid skapandet av frågan.";
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize(Roles = "Kommenterare,SuperAdmin")]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("ContentCreation")]
        public async Task<IActionResult> CreateAnswer(int questionId, string content, IFormFile attachment)

        {
            var currentUserId = _userManager.GetUserId(User);

            if (string.IsNullOrEmpty(currentUserId))
            {
                return RedirectToAction("Login", "Home");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                TempData["ErrorMessage"] = "Svar kan inte vara tomt.";
                return RedirectToAction("Index");
            }

            // Sanitize and validate content (XSS prevention)
            content = SanitizeInput(content);

            if (ContainsDangerousContent(content))
            {
                TempData["ErrorMessage"] = "Svaret innehåller potentiellt farliga element och kan inte accepteras.";
                return RedirectToAction("Index");
            }

            if (content.Length > 2000)
            {
                TempData["ErrorMessage"] = "Svaret är för långt.";
                return RedirectToAction("Index");
            }

            string attachmentFileName = null;
            string attachmentPath = null;
            
            // File upload validation (same as CreateQuestion)
            if (attachment != null && attachment.Length > 0)
            {
                var validationResult = await ValidateFileUploadAsync(attachment);
                if (!validationResult.IsValid)
                {
                    TempData["ErrorMessage"] = validationResult.ErrorMessage;
                    return RedirectToAction("Index");
                }
                
                attachmentFileName = SanitizeFileName(attachment.FileName);
                var fileName = $"{Guid.NewGuid()}{Path.GetExtension(attachmentFileName).ToLower()}";
                var uploadsPath = Path.Combine("wwwroot", "uploads");
                
                if (!Directory.Exists(uploadsPath))
                    Directory.CreateDirectory(uploadsPath);
                    
                attachmentPath = Path.Combine("uploads", fileName);
                var fullPath = Path.Combine("wwwroot", attachmentPath);
                
                using(var stream = new FileStream(fullPath, FileMode.Create))
                {
                    await attachment.CopyToAsync(stream);
                }
            }

            try
            {
                var answer = new Answer
                {
                    Content = content.Trim(),
                    QuestionId = questionId,
                    UserId = currentUserId,
                    AttachmentFileName = attachmentFileName,
                    AttachmentPath = attachmentPath,
                    CreatedDate = DateTime.Now
                };

                _context.Answers.Add(answer);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = "Ditt svar har publicerats!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating answer");
                TempData["ErrorMessage"] = "Ett fel uppstod vid skapandet av svaret.";
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken] // CRITICAL: CSRF protection as per Chapter 6
        public async Task<IActionResult> LikeQuestion(int questionId)
        {
            var currentUserId = _userManager.GetUserId(User);
            var currentUsername = User.Identity?.Name ?? "Unknown";
            
            if (string.IsNullOrEmpty(currentUserId))
            {
                return RedirectToAction("Login", "Home");
            }

            try
            {
                // Check if user already liked this question
                var existingLike = await _context.Likes
                    .FirstOrDefaultAsync(l => l.QuestionId == questionId && l.UserId == currentUserId);

                if (existingLike != null)
                {
                    TempData["ErrorMessage"] = "Du har redan gillat denna fråga.";
                    return RedirectToAction("Index");
                }
                
                var like = new Like
                {
                    QuestionId = questionId,
                    UserId = currentUserId,
                    OriginalUsername = currentUsername,
                    CreatedDate = DateTime.Now
                };

                _context.Likes.Add(like);

                // Update question like count
                var question = await _context.Questions.FindAsync(questionId);
                if (question != null)
                {
                    question.Likes++;
                    await _context.SaveChangesAsync();
                    TempData["SuccessMessage"] = "Du gillade frågan!";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error liking question");
                TempData["ErrorMessage"] = "Ett fel uppstod vid gillande av frågan.";
            }

            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> Search([FromQuery] string searchTerm)
        {
            ViewBag.CurrentUsername = User.Identity?.Name ?? "Unknown";
            ViewBag.UserRole = User.IsInRole("SuperAdmin") ? "SuperAdmin" : "User";
            ViewBag.SearchTerm = searchTerm;

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return RedirectToAction("Index");
            }

            // Sanitize search term to prevent XSS in search results
            searchTerm = SanitizeInput(searchTerm);

            try
            {
                var searchResults = await _context.Questions
                    .Where(q => q.Title.Contains(searchTerm) || q.Content.Contains(searchTerm))
                    .Include(q => q.Answers)
                    .OrderByDescending(q => q.Likes)
                    .ThenByDescending(q => q.CreatedDate)
                    .Take(20)
                    .ToListAsync();

                // Get usernames
                var allUserIds = searchResults.Select(q => q.UserId)
                    .Concat(searchResults.SelectMany(q => q.Answers.Select(a => a.UserId)))
                    .Where(id => !string.IsNullOrEmpty(id));

                var userNames = await GetUserNamesDictionaryAsync(allUserIds);

                // Decode HTML entities
                foreach (var question in searchResults)
                {
                    DecodeHtmlEntities(question);
                }

                // Get ongoing contests with leaderboard data
                var contests = await _context.Contests
                    .Where(c => c.IsActive && c.EndDate > DateTime.Now)
                    .OrderBy(c => c.EndDate)
                    .Take(3)
                    .ToListAsync();

                var contestLeaderboards = await BuildLiveContestLeaderboardsAsync(contests, 3);

                ViewBag.ContestLeaderboards = contestLeaderboards;
                // Decode HTML entities for contests
                foreach (var contest in contests)
                {
                    DecodeHtmlEntities(contest);
                }

                ViewBag.SearchResults = searchResults;
                ViewBag.Contests = contests;
                ViewBag.UserNames = userNames;

                return View("Search");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing search");
                TempData["ErrorMessage"] = "Ett fel uppstod vid sökning.";
                return RedirectToAction("Index");
            }
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken] // CRITICAL: CSRF protection as per Chapter 6
        public async Task<IActionResult> ToggleFAQ(int questionId)
        {
            try
            {
                var question = await _context.Questions.FindAsync(questionId);
                if (question != null)
                {
                    question.IsFrequentlyAsked = !question.IsFrequentlyAsked;
                    await _context.SaveChangesAsync();

                    string status = question.IsFrequentlyAsked ? "tillagd i" : "borttagen från";
                    TempData["SuccessMessage"] = $"Frågan har {status} vanliga frågor.";
                }
                else
                {
                    TempData["ErrorMessage"] = "Frågan hittades inte.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling FAQ status");
                TempData["ErrorMessage"] = "Ett fel uppstod vid hantering av FAQ-status.";
            }

            return RedirectToAction("Index");
        }

        // NEW: Admin functionality to delete questions and answers
        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken] // CRITICAL: CSRF protection as per Chapter 6
        public async Task<IActionResult> DeleteQuestion(int questionId)
        {
            try
            {
                var question = await _context.Questions
                    .Include(q => q.Answers) // Include answers for deletion
                    .FirstOrDefaultAsync(q => q.Id == questionId);

                if (question == null)
                {
                    TempData["ErrorMessage"] = "Frågan hittades inte.";
                    return RedirectToAction("Index");
                }

                // Delete associated file if it exists
                if (!string.IsNullOrEmpty(question.AttachmentPath))
                {
                    var filePath = Path.Combine("wwwroot", question.AttachmentPath);
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }

                // Delete associated answer files
                foreach (var answer in question.Answers)
                {
                    if (!string.IsNullOrEmpty(answer.AttachmentPath))
                    {
                        var answerFilePath = Path.Combine("wwwroot", answer.AttachmentPath);
                        if (System.IO.File.Exists(answerFilePath))
                        {
                            System.IO.File.Delete(answerFilePath);
                        }
                    }
                }

                // EF Core will handle cascading deletes for Answers and Likes automatically
                _context.Questions.Remove(question);
                await _context.SaveChangesAsync();

                // Log the admin action for security audit
                _logger.LogWarning($"ADMIN: Question '{question.Title}' (ID: {questionId}) deleted by admin {User.Identity.Name}");
                
                TempData["SuccessMessage"] = $"Frågan '{question.Title}' har tagits bort.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting question");
                TempData["ErrorMessage"] = "Ett fel uppstod vid borttagning av frågan.";
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken] // CRITICAL: CSRF protection as per Chapter 6
        public async Task<IActionResult> DeleteAnswer(int answerId, int questionId)
        {
            try
            {
                var answer = await _context.Answers.FindAsync(answerId);

                if (answer == null)
                {
                    TempData["ErrorMessage"] = "Svaret hittades inte.";
                    return RedirectToAction("Index");
                }

                // Delete associated file if it exists
                if (!string.IsNullOrEmpty(answer.AttachmentPath))
                {
                    var filePath = Path.Combine("wwwroot", answer.AttachmentPath);
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }

                _context.Answers.Remove(answer);
                await _context.SaveChangesAsync();

                // Log the admin action for security audit
                _logger.LogWarning($"ADMIN: Answer (ID: {answerId}) deleted by admin {User.Identity.Name}");
                
                TempData["SuccessMessage"] = "Svaret har tagits bort.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting answer");
                TempData["ErrorMessage"] = "Ett fel uppstod vid borttagning av svaret.";
            }

            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> QuestionDetail(int id)
        {
            try
            {
                var question = await _context.Questions
                    .Include(q => q.Answers.OrderBy(a => a.CreatedDate))
                    .FirstOrDefaultAsync(q => q.Id == id);

                if (question == null)
                {
                    TempData["ErrorMessage"] = "Frågan hittades inte.";
                    return RedirectToAction("Index");
                }

                // Decode HTML entities
                DecodeHtmlEntities(question);

                // Get usernames for question and answers
                var allUserIds = new List<string> { question.UserId }
                    .Concat(question.Answers.Select(a => a.UserId))
                    .Where(id => !string.IsNullOrEmpty(id));

                var userNames = await GetUserNamesDictionaryAsync(allUserIds);

                ViewBag.CurrentUsername = User.Identity?.Name ?? "Unknown";
                ViewBag.UserRole = User.IsInRole("SuperAdmin") ? "SuperAdmin" : "User";
                ViewBag.UserNames = userNames;

                return View(question);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading question detail");
                TempData["ErrorMessage"] = "Ett fel uppstod vid inladdning av frågan.";
                return RedirectToAction("Index");
            }
        }

        // Contest management methods
        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateContest(string name, string description, DateTime startDate, DateTime endDate)
        {
            try
            {
                // Input validation
                if (string.IsNullOrWhiteSpace(name))
                {
                    TempData["ErrorMessage"] = "Tävlingsnamn är obligatoriskt.";
                    return RedirectToAction("AdminContests");
                }

                // Sanitize inputs
                name = SanitizeInput(name);
                description = string.IsNullOrWhiteSpace(description) ? null : SanitizeInput(description);

                // Date validation
                if (startDate >= endDate)
                {
                    TempData["ErrorMessage"] = "Slutdatum måste vara efter startdatum.";
                    return RedirectToAction("AdminContests");
                }

                if (endDate <= DateTime.Now)
                {
                    TempData["ErrorMessage"] = "Slutdatum måste vara i framtiden.";
                    return RedirectToAction("AdminContests");
                }

                var contest = new Contest
                {
                    Name = name.Trim(),
                    Description = description?.Trim(),
                    StartDate = startDate,
                    EndDate = endDate,
                    IsActive = true,
                    CreatedDate = DateTime.Now
                };

                _context.Contests.Add(contest);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"Tävling '{name}' har skapats!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating contest");
                TempData["ErrorMessage"] = "Ett fel uppstod vid skapandet av tävlingen.";
            }

            return RedirectToAction("AdminContests");
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteContest(int contestId)
        {
            try
            {
                var contest = await _context.Contests
                    .FirstOrDefaultAsync(c => c.Id == contestId);

                if (contest == null)
                {
                    TempData["ErrorMessage"] = "Tävlingen hittades inte.";
                    return RedirectToAction("AdminContests");
                }

                // Remove contest entries first
                var entries = await _context.ContestEntries
                    .Where(ce => ce.ContestId == contestId)
                    .ToListAsync();
                
                _context.ContestEntries.RemoveRange(entries);
                
                // Remove the contest
                _context.Contests.Remove(contest);
                await _context.SaveChangesAsync();

                _logger.LogWarning($"ADMIN: Contest '{contest.Name}' deleted by {User.Identity.Name}");
                TempData["SuccessMessage"] = $"Tävlingen '{contest.Name}' har tagits bort.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting contest");
                TempData["ErrorMessage"] = "Ett fel uppstod vid borttagning av tävlingen.";
            }

            return RedirectToAction("AdminContests");
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateDeals(int contestId, string userId, int dealsCount)
        {
            try
            {
                // Validation
                if (string.IsNullOrEmpty(userId) || dealsCount < 0)
                {
                    TempData["ErrorMessage"] = "Ogiltiga värden för affärsuppdatering.";
                    return RedirectToAction("AdminContests");
                }

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    TempData["ErrorMessage"] = "Användaren hittades inte.";
                    return RedirectToAction("AdminContests");
                }

                var contest = await _context.Contests.FindAsync(contestId);
                if (contest == null)
                {
                    TempData["ErrorMessage"] = "Tävlingen hittades inte.";
                    return RedirectToAction("AdminContests");
                }

                // Find or create contest entry
                var entry = await _context.ContestEntries
                    .FirstOrDefaultAsync(ce =>
                        ce.ContestId == contestId &&
                        (ce.UserId == userId || ce.EmployeeNumber == user.UserName));

                if (entry == null)
                {
                    entry = new ContestEntry
                    {
                        ContestId = contestId,
                        UserId = userId,
                        EmployeeNumber = user.UserName,
                        DealsCount = dealsCount,
                        UpdatedDate = DateTime.Now
                    };
                    _context.ContestEntries.Add(entry);
                }
                else
                {
                    entry.DealsCount = dealsCount;
                    entry.UpdatedDate = DateTime.Now;
                }

                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Affärer uppdaterade för {user.UserName}!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating deals");
                TempData["ErrorMessage"] = "Ett fel uppstod vid uppdatering av affärer.";
            }

            return RedirectToAction("AdminContests");
        }

        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> AdminContests()
        {
            try
            {
                var contests = await _context.Contests
                    .OrderByDescending(c => c.CreatedDate)
                    .ToListAsync();

                // Decode HTML entities for contests
                foreach (var contest in contests)
                {
                    DecodeHtmlEntities(contest);
                }

                var allUsers = await _userManager.Users.ToListAsync();
                ViewBag.AllUsers = allUsers;

                return View(contests);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading admin contests");
                TempData["ErrorMessage"] = "Ett fel uppstod vid laddning av tävlingar.";
                return RedirectToAction("Index");
            }
        }

        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> ContestLeaderboard(int id)
        {
            try
            {
                var contest = await _context.Contests.FirstOrDefaultAsync(c => c.Id == id);
                if (contest == null)
                {
                    TempData["ErrorMessage"] = "Tävlingen hittades inte.";
                    return RedirectToAction("Index");
                }

                DecodeHtmlEntities(contest);

                var entries = await BuildLiveContestEntriesForContestAsync(contest);

                ViewBag.CurrentUsername = User.Identity?.Name ?? "Unknown";
                ViewBag.UserRole = User.IsInRole("SuperAdmin") ? "SuperAdmin" : "User";

                var model = new ContestLeaderboardViewModel
                {
                    Contest = contest,
                    Entries = entries
                };

                return View(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading contest leaderboard for contest {ContestId}", id);
                TempData["ErrorMessage"] = "Ett fel uppstod vid laddning av leaderboard.";
                return RedirectToAction("Index");
            }
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncHubSpotDeals()
        {
            try
            {
                var result = await _hubSpotSyncService.RunIncrementalSyncAsync();
                if (result.Succeeded)
                {
                    TempData["SuccessMessage"] =
                        $"HubSpot-sync klar. Hämtade: {result.DealsFetched}, Nya: {result.DealsImported}, Uppdaterade: {result.DealsUpdated}, Hoppade över: {result.DealsSkipped}.";
                }
                else
                {
                    TempData["ErrorMessage"] = $"HubSpot-sync misslyckades: {result.Message}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual HubSpot sync failed");
                TempData["ErrorMessage"] = "Ett fel uppstod under HubSpot-sync.";
            }

            return RedirectToAction("AdminContests");
        }

        [HttpGet]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> HubSpotOwnerMappings(string? searchTerm, bool showUnmappedOnly = false)
        {
            try
            {
                var mappingsQuery = _context.HubSpotOwnerMappings.AsNoTracking().AsQueryable();

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    var term = searchTerm.Trim();
                    mappingsQuery = mappingsQuery.Where(m =>
                        m.HubSpotOwnerId.Contains(term) ||
                        (m.HubSpotOwnerEmail != null && m.HubSpotOwnerEmail.Contains(term)) ||
                        (m.HubSpotFirstName != null && m.HubSpotFirstName.Contains(term)) ||
                        (m.HubSpotLastName != null && m.HubSpotLastName.Contains(term)) ||
                        (m.OwnerUsername != null && m.OwnerUsername.Contains(term)));
                }

                if (showUnmappedOnly)
                {
                    mappingsQuery = mappingsQuery.Where(m => m.OwnerUserId == null);
                }

                var mappings = await mappingsQuery
                    .OrderByDescending(m => m.LastSeenUtc)
                    .ToListAsync();

                var users = await _userManager.Users
                    .OrderBy(u => u.UserName)
                    .Select(u => new HubSpotUserOptionViewModel
                    {
                        Id = u.Id,
                        Username = u.UserName ?? string.Empty,
                        Email = u.Email
                    })
                    .ToListAsync();

                var model = new HubSpotOwnerMappingsAdminViewModel
                {
                    SearchTerm = searchTerm?.Trim() ?? string.Empty,
                    ShowUnmappedOnly = showUnmappedOnly,
                    UserOptions = users,
                    OwnerMappings = mappings.Select(m => new HubSpotOwnerMappingRowViewModel
                    {
                        HubSpotOwnerId = m.HubSpotOwnerId,
                        HubSpotOwnerEmail = m.HubSpotOwnerEmail,
                        HubSpotFirstName = m.HubSpotFirstName,
                        HubSpotLastName = m.HubSpotLastName,
                        IsArchived = m.IsArchived,
                        LastSeenUtc = m.LastSeenUtc,
                        LastOwnerSyncUtc = m.LastOwnerSyncUtc,
                        OwnerUserId = m.OwnerUserId,
                        OwnerUsername = m.OwnerUsername
                    }).ToList()
                };

                return View(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading HubSpot owner mappings");
                TempData["ErrorMessage"] = "Ett fel uppstod vid laddning av HubSpot owner-kopplingar.";
                return RedirectToAction("AdminContests");
            }
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateHubSpotOwnerMapping(
            string hubSpotOwnerId,
            string? ownerUserId,
            string? searchTerm,
            bool showUnmappedOnly = false)
        {
            if (string.IsNullOrWhiteSpace(hubSpotOwnerId))
            {
                TempData["ErrorMessage"] = "HubSpot owner-ID saknas.";
                return RedirectToAction(nameof(HubSpotOwnerMappings), new { searchTerm, showUnmappedOnly });
            }

            var mapping = await _context.HubSpotOwnerMappings
                .FirstOrDefaultAsync(m => m.HubSpotOwnerId == hubSpotOwnerId);

            if (mapping == null)
            {
                TempData["ErrorMessage"] = $"HubSpot owner {hubSpotOwnerId} hittades inte.";
                return RedirectToAction(nameof(HubSpotOwnerMappings), new { searchTerm, showUnmappedOnly });
            }

            var targetUserId = string.IsNullOrWhiteSpace(ownerUserId)
                ? null
                : ownerUserId.Trim();

            if (targetUserId == null)
            {
                mapping.OwnerUserId = null;
                mapping.OwnerUsername = null;
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"Kopplingen för owner {hubSpotOwnerId} har tagits bort.";
                return RedirectToAction(nameof(HubSpotOwnerMappings), new { searchTerm, showUnmappedOnly });
            }

            var targetUser = await _userManager.FindByIdAsync(targetUserId);
            if (targetUser == null)
            {
                TempData["ErrorMessage"] = "Vald användare hittades inte.";
                return RedirectToAction(nameof(HubSpotOwnerMappings), new { searchTerm, showUnmappedOnly });
            }

            var existingUserMapping = await _context.HubSpotOwnerMappings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.OwnerUserId == targetUserId && m.HubSpotOwnerId != hubSpotOwnerId);
            if (existingUserMapping != null)
            {
                TempData["ErrorMessage"] =
                    $"Användaren {targetUser.UserName} är redan kopplad till owner {existingUserMapping.HubSpotOwnerId}.";
                return RedirectToAction(nameof(HubSpotOwnerMappings), new { searchTerm, showUnmappedOnly });
            }

            mapping.OwnerUserId = targetUser.Id;
            mapping.OwnerUsername = targetUser.UserName;
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"Owner {hubSpotOwnerId} är nu kopplad till användare {targetUser.UserName}.";
            return RedirectToAction(nameof(HubSpotOwnerMappings), new { searchTerm, showUnmappedOnly });
        }

        [HttpGet]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> GetContestEntries(int contestId)
        {
            try
            {
                var contest = await _context.Contests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == contestId);
                if (contest == null)
                {
                    return Json(new { error = "Tävlingen hittades inte." });
                }

                var entries = await BuildLiveContestEntriesForContestAsync(contest);
                var payload = entries.Select(ce => new
                {
                    ce.EmployeeNumber,
                    ce.DealsCount,
                    UpdatedDate = ce.UpdatedDate.ToString("yyyy-MM-dd HH:mm")
                });

                return Json(payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contest entries");
                return Json(new { error = "Ett fel uppstod vid laddning av data" });
            }
        }

        // Security helper methods (Chapter 6 & 7 best practices)
        private string SanitizeInput(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            // HTML encode to prevent XSS (Chapter 6 recommendation)
            input = WebUtility.HtmlEncode(input);
            
            // Remove potentially dangerous characters
            input = input.Replace("<", "&lt;").Replace(">", "&gt;");
            
            return input.Trim();
        }

        private bool ContainsDangerousContent(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;

            // Patterns to detect XSS attempts (Chapter 6 security practices)
            var dangerousPatterns = new[]
            {
                @"<script[^>]*>.*?</script>",
                @"javascript:",
                @"vbscript:",
                @"onload\s*=",
                @"onerror\s*=",
                @"onclick\s*=",
                @"onmouseover\s*=",
                @"onload\s*=",
                @"<iframe",
                @"<object",
                @"<embed",
                @"<form",
                @"document\.",
                @"window\.",
                @"eval\s*\(",
                @"alert\s*\(",
                @"confirm\s*\(",
                @"prompt\s*\("
            };

            foreach (var pattern in dangerousPatterns)
            {
                if (Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase))
                {
                    _logger.LogWarning($"Dangerous content detected: {pattern} in input: {input.Substring(0, Math.Min(50, input.Length))}...");
                    return true;
                }
            }

            return false;
        }

        private async Task<(bool IsValid, string ErrorMessage)> ValidateFileUploadAsync(IFormFile file)
        {
            // File type validation with MIME type checking (enhanced security following Chapter 6)
            var allowedTypes = new Dictionary<string, string[]>
            {
                [".pdf"] = new[] { "application/pdf" },
                [".doc"] = new[] { "application/msword" },
                [".docx"] = new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
                [".png"] = new[] { "image/png" },
                [".jpg"] = new[] { "image/jpeg" },
                [".jpeg"] = new[] { "image/jpeg" }
            };

            var extension = Path.GetExtension(file.FileName).ToLower();
            
            if (!allowedTypes.ContainsKey(extension))
            {
                return (false, "Endast PDF, Word-dokument och bilder tillåts.");
            }

            // MIME type validation - critical security check from Chapter 6
            if (!allowedTypes[extension].Contains(file.ContentType))
            {
                _logger.LogWarning($"SECURITY: File extension {extension} doesn't match MIME type {file.ContentType} for file {file.FileName}");
                return (false, "Filtypen matchar inte filinnehållet.");
            }

            // File size validation (5MB limit)
            if (file.Length > 5 * 1024 * 1024)
            {
                return (false, "Filen får inte vara större än 5MB.");
            }

            // Enhanced file content validation - read file signature (magic bytes)
            var contentValidation = await ValidateFileContentAsync(file, extension);
            if (!contentValidation.IsValid)
            {
                return contentValidation;
            }

            // Filename validation - prevent path traversal
            var fileName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
            {
                return (false, "Ogiltigt filnamn.");
            }

            // Check for dangerous patterns in filename
            var dangerousPatterns = new[] { "..", "/", "\\", ":", "*", "?", "\"", "<", ">", "|" };
            if (dangerousPatterns.Any(pattern => fileName.Contains(pattern)))
            {
                return (false, "Filnamnet innehåller otillåtna tecken.");
            }

            // Anti-virus style check - only scan text-based files for embedded scripts
            // Skip binary files like images to avoid false positives
            if (ShouldScanFileContent(extension))
            {
                var scriptValidation = await CheckForEmbeddedScriptsAsync(file);
                if (!scriptValidation.IsValid)
                {
                    return scriptValidation;
                }
            }

            return (true, string.Empty);
        }

        private async Task<(bool IsValid, string ErrorMessage)> ValidateFileContentAsync(IFormFile file, string extension)
        {
            // Read the first few bytes to check file signatures (magic bytes)
            // This prevents attackers from uploading malicious files with fake extensions
            var buffer = new byte[20]; // Read first 20 bytes
            
            using (var stream = file.OpenReadStream())
            {
                await stream.ReadAsync(buffer, 0, buffer.Length);
                stream.Position = 0; // Reset stream position
            }

            // Check file signatures based on extension
            switch (extension)
            {
                case ".pdf":
                    // PDF files start with "%PDF"
                    if (buffer.Length >= 4 && 
                        buffer[0] == 0x25 && buffer[1] == 0x50 && 
                        buffer[2] == 0x44 && buffer[3] == 0x46)
                    {
                        return (true, string.Empty);
                    }
                    break;

                case ".png":
                    // PNG files start with specific 8-byte signature
                    if (buffer.Length >= 8 &&
                        buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47 &&
                        buffer[4] == 0x0D && buffer[5] == 0x0A && buffer[6] == 0x1A && buffer[7] == 0x0A)
                    {
                        return (true, string.Empty);
                    }
                    break;

                case ".jpg":
                case ".jpeg":
                    // JPEG files start with FFD8 and end with FFD9
                    if (buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xD8)
                    {
                        return (true, string.Empty);
                    }
                    break;

                case ".doc":
                    // DOC files start with D0CF11E0 (OLE compound document)
                    if (buffer.Length >= 4 &&
                        buffer[0] == 0xD0 && buffer[1] == 0xCF && 
                        buffer[2] == 0x11 && buffer[3] == 0xE0)
                    {
                        return (true, string.Empty);
                    }
                    break;

                case ".docx":
                    // DOCX files are ZIP archives, start with "PK"
                    if (buffer.Length >= 2 && buffer[0] == 0x50 && buffer[1] == 0x4B)
                    {
                        return (true, string.Empty);
                    }
                    break;

                default:
                    return (false, "Okänd filtyp.");
            }

            _logger.LogWarning($"SECURITY: File signature validation failed for {file.FileName} with extension {extension}");
            return (false, "Filens innehåll matchar inte den angivna filtypen.");
        }

        private bool ShouldScanFileContent(string extension)
        {
            // Only scan file types that can potentially contain executable content
            // Images are binary and should not be scanned as text (causes false positives)
            var textBasedFiles = new[] { ".pdf", ".doc", ".docx" };
            return textBasedFiles.Contains(extension);
        }

        private async Task<(bool IsValid, string ErrorMessage)> CheckForEmbeddedScriptsAsync(IFormFile file)
        {
            // Read file content to check for embedded scripts or malicious content
            // This should only be called for text-based files like PDFs and Office documents
            try
            {
                using var reader = new StreamReader(file.OpenReadStream());
                var content = await reader.ReadToEndAsync();
                
                // Check for common script injection patterns in document files
                var dangerousPatterns = new[]
                {
                    "<script", "javascript:", "vbscript:", "onload=", "onerror=",
                    "<?php", "<%", "<jsp:", "eval(", "exec(", "system(",
                    "/bin/sh", "cmd.exe", "powershell"
                };

                foreach (var pattern in dangerousPatterns)
                {
                    if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning($"SECURITY: Dangerous content pattern '{pattern}' found in document file {file.FileName}");
                        return (false, "Dokumentet innehåller potentiellt skadligt innehåll.");
                    }
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                // If we can't read the file as text, that's actually fine for some document formats
                _logger.LogDebug($"Could not scan file content for {file.FileName}: {ex.Message}");
                return (true, string.Empty);
            }
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return string.Empty;

            // Remove dangerous characters from filename
            var sanitized = Regex.Replace(fileName, @"[<>:""/\\|?*]", "_");
            
            // Limit length
            if (sanitized.Length > 200)
                sanitized = sanitized.Substring(0, 200);

            return sanitized;
        }
        
        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GeKommenterare(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                TempData["ErrorMessage"] = "Ogiltigt användar-ID.";
                return RedirectToAction("Admin", "Home");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                TempData["ErrorMessage"] = "Användaren hittades inte.";
                return RedirectToAction("Admin", "Home");
            }

            if (!await _userManager.IsInRoleAsync(user, "Kommenterare"))
            {
                var result = await _userManager.AddToRoleAsync(user, "Kommenterare");
                if (!result.Succeeded)
                {
                    TempData["ErrorMessage"] = "Kunde inte lägga till rollen Kommenterare.";
                    return RedirectToAction("Admin", "Home");
                }

                _logger.LogInformation("ADMIN: {Admin} gav Kommenterare-roll till {User}.", User.Identity?.Name, user.UserName);
                TempData["SuccessMessage"] = $"Användaren {user.UserName} har nu rollen Kommenterare.";
            }
            else
            {
                TempData["InfoMessage"] = $"Användaren {user.UserName} har redan rollen Kommenterare.";
            }

            return RedirectToAction("Admin", "Home");
        }

        [HttpPost]
        [Authorize(Roles = "SuperAdmin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TaBortKommenterare(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                TempData["ErrorMessage"] = "Ogiltigt användar-ID.";
                return RedirectToAction("Admin", "Home");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                TempData["ErrorMessage"] = "Användaren hittades inte.";
                return RedirectToAction("Admin", "Home");
            }

            if (await _userManager.IsInRoleAsync(user, "Kommenterare"))
            {
                var result = await _userManager.RemoveFromRoleAsync(user, "Kommenterare");
                if (!result.Succeeded)
                {
                    TempData["ErrorMessage"] = "Kunde inte ta bort rollen Kommenterare.";
                    return RedirectToAction("Admin", "Home");
                }

                _logger.LogInformation("ADMIN: {Admin} tog bort Kommenterare-roll från {User}.", User.Identity?.Name, user.UserName);
                TempData["SuccessMessage"] = $"Användaren {user.UserName} har inte längre rollen Kommenterare.";
            }
            else
            {
                TempData["InfoMessage"] = $"Användaren {user.UserName} har inte rollen Kommenterare.";
            }

            return RedirectToAction("Admin", "Home");
        }

    }
}
