using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace WebApplication2.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendQuestionNotificationAsync(string category, string questionTitle, string questionContent, string askedBy, string questionUrl)
        {
            try
            {
                var emailSettings = _configuration.GetSection("EmailSettings");
                var smtpHost = emailSettings["SmtpHost"];
                var smtpPort = int.Parse(emailSettings["SmtpPort"] ?? "587");
                var smtpUsername = emailSettings["SmtpUsername"];
                var smtpPassword = emailSettings["SmtpPassword"];
                var fromEmail = emailSettings["FromEmail"];
                var fromName = emailSettings["FromName"];

                if (string.IsNullOrEmpty(smtpHost) || string.IsNullOrEmpty(smtpUsername) || string.IsNullOrEmpty(smtpPassword))
                {
                    _logger.LogWarning("Email settings are not configured. Skipping email notification.");
                    return;
                }

                string recipientEmail = GetRecipientEmail(category);
                
                if (string.IsNullOrEmpty(recipientEmail))
                {
                    _logger.LogWarning($"No recipient email configured for category: {category}");
                    return;
                }

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(fromName, fromEmail));
                message.To.Add(new MailboxAddress("", recipientEmail));
                message.Subject = $"Ny {category}: {questionTitle}";

		var bodyBuilder = new BodyBuilder();
                bodyBuilder.HtmlBody = $@"
                    <html>
                    <head>
                        <style>
                            body {{ 
                                font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
                                line-height: 1.6; 
                                color: #333; 
                                background-color: #f8f9fa;
                                margin: 0;
                                padding: 0;
                            }}
                            .email-container {{ 
                                max-width: 600px; 
                                margin: 40px auto; 
                                background-color: #ffffff;
                                border-radius: 8px;
                                overflow: hidden;
                                box-shadow: 0 2px 10px rgba(0,0,0,0.1);
                            }}
                            .header {{ 
                                background-color: #095EA9; 
                                color: white; 
                                padding: 30px 20px; 
                                text-align: center;
                            }}
                            .header h1 {{
                                margin: 0;
                                font-size: 24px;
                                font-weight: 600;
                            }}
                            .content {{ 
                                padding: 30px;
                                background-color: #ffffff;
                            }}
                            .question-card {{
                                border: 1px solid #dee2e6;
                                border-radius: 8px;
                                padding: 20px;
                                background-color: #ffffff;
                                margin: 20px 0;
                            }}
                            .badge {{ 
                                display: inline-block;
                                padding: 6px 12px; 
                                background-color: #17a2b8; 
                                color: white; 
                                border-radius: 4px; 
                                font-size: 12px;
                                font-weight: 600;
                                text-transform: uppercase;
                            }}
                            .meta-info {{
                                color: #6c757d;
                                font-size: 14px;
                                margin: 10px 0;
                                padding-bottom: 15px;
                                border-bottom: 1px solid #e9ecef;
                            }}
                            .question-title {{ 
                                color: #095EA9; 
                                font-size: 20px;
                                font-weight: 600;
                                margin: 15px 0 10px 0;
                                line-height: 1.4;
                            }}
                            .question-content {{ 
                                color: #212529;
                                font-size: 15px;
                                line-height: 1.6;
                                padding: 15px 0;
                            }}
                            .button {{ 
                                display: inline-block; 
                                padding: 14px 32px; 
                                background-color: #28a745; 
                                color: white; 
                                text-decoration: none; 
                                border-radius: 5px; 
                                font-weight: 600;
                                font-size: 16px;
                                margin: 20px 0;
                                transition: background-color 0.3s ease;
                            }}
                            .button:hover {{ 
                                background-color: #218838;
                                color: white;
                                text-decoration: none;
                            }}
                            .button-container {{
                                text-align: center;
                                padding: 20px 0;
                            }}
                            .footer {{ 
                                text-align: center; 
                                padding: 20px; 
                                color: #6c757d; 
                                font-size: 13px;
                                background-color: #f8f9fa;
                                border-top: 1px solid #dee2e6;
                            }}
                            .footer-logo {{
                                color: #095EA9;
                                font-weight: 600;
                                font-size: 16px;
                                margin-bottom: 5px;
                            }}
                        </style>
                    </head>
                    <body>
                        <div class='email-container'>
                            <div class='header'>
                                <h1>Ny fråga i STL Forum</h1>
                            </div>
                            <div class='content'>
                                <div class='meta-info'>
                                    <strong>Kategori:</strong> <span class='badge'>{System.Net.WebUtility.HtmlEncode(category)}</span><br>
                                    <strong>Ställd av:</strong> {System.Net.WebUtility.HtmlEncode(askedBy)}<br>
                                    <strong>Datum:</strong> {DateTime.Now:dd MMMM yyyy, HH:mm}
                                </div>
                                
                                <div class='question-card'>
                                    <h2 class='question-title'>{System.Net.WebUtility.HtmlEncode(questionTitle)}</h2>
                                    
                                    <div class='question-content'>
                                        {System.Net.WebUtility.HtmlEncode(questionContent)}
                                    </div>
                                </div>
                                
                                <div class='button-container'>
                                    <a href='{questionUrl}' class='button'>Visa frågan i forumet →</a>
                                </div>
                            </div>
                            <div class='footer'>
                                <div class='footer-logo'>STL Forum</div>
                                <p>Detta är en automatisk notifiering från STL Forum.<br>
                                Svara inte på detta mail.</p>
                            </div>
                        </div>
                    </body>
                    </html>";

                message.Body = bodyBuilder.ToMessageBody();

                using (var client = new SmtpClient())
                {
                    await client.ConnectAsync(smtpHost, smtpPort, SecureSocketOptions.StartTls);
                    await client.AuthenticateAsync(smtpUsername, smtpPassword);
                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);
                }

                _logger.LogInformation($"Email notification sent successfully for {category} to {recipientEmail}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send email notification for category: {category}");
            }
        }

        private string GetRecipientEmail(string category)
        {
            var emailSettings = _configuration.GetSection("EmailSettings");
            
            return category?.ToLower() switch
            {
                "driftfråga" => emailSettings["DriftfragaEmail"],
                "säljfråga" => emailSettings["SaljfragaEmail"],
                _ => null
            };
        }
    }
}

