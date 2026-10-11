using Bank.Notification.Api.Application.External.SendGridEmail;
using System.Net.Http.Headers;

namespace Bank.Notification.Api.External.SendGridEmail;

public class SendGridEmailService: ISendGridEmailService
{
    private readonly IConfiguration _configuration;

    public SendGridEmailService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<bool> Execute(string payload)
    {
        var sendGridApiKey = _configuration["SENDGRIDAPIKEY"];
        var sendGridEndpoint = _configuration["SENDGRIDAPIURL"];

        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sendGridApiKey);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(sendGridEndpoint, content);
            return response.IsSuccessStatusCode;
        }
    }
}
