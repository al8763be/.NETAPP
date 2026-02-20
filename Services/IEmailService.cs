namespace WebApplication2.Services
{
    public interface IEmailService
    {
        Task SendQuestionNotificationAsync(string category, string questionTitle, string questionContent, string askedBy, string questionUrl);
    }
}
