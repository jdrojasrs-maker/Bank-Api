namespace Bank.Notification.Api.Application.Models;

public static class CreateSendGridModel
{
    public static string Create(string status, string message, string fromEmail, string toEmail)
    {
        string htmlTemplate = File.ReadAllText("Template/email-template.html");
        string htmlContent = htmlTemplate.Replace("{{STATUS}}", status)
                                     .Replace("{{MESSAGE}}", message)
                                     .Replace("{{FROMEMAIL}}", fromEmail)
                                     .Replace("{{TOEMAIL}}", toEmail)
                                     .Replace("{{DATE}}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        
        var emailPayload = new
        {
            from = new { email = fromEmail },
            personalizations = new List<object>
            {
                new
                {
                    to = new List<object>
                    {
                        new { email = toEmail }
                    },
                    subject = "Bank Transfer Notification"
                }
            },
            content = new List<object>
            {
                new
                {
                    type = "text/html",
                    value = htmlContent
                }
            }
        };

        return System.Text.Json.JsonSerializer.Serialize(emailPayload);
    }
}
