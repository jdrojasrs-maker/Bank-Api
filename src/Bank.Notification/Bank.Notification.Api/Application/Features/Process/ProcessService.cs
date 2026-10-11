using Bank.Notification.Api.Application.Database;
using Bank.Notification.Api.Application.External.SendGridEmail;
using Bank.Notification.Api.Domain.Constants;
using Bank.Notification.Api.Domain.Entities;
using Bank.Notification.Api.Application.Models;
using Newtonsoft.Json;

namespace Bank.Notification.Api.Application.Features.Process;

public class ProcessService: IProcessService
{
    private readonly IDatabaseService _db;
    private readonly ISendGridEmailService _sendGridEmailService;
    private readonly IConfiguration _configuration;
    public ProcessService(IDatabaseService db, ISendGridEmailService sendGridEmailService, IConfiguration configuration)
    {
        _db = db;
        _sendGridEmailService = sendGridEmailService;
        _configuration = configuration;
    }

    public async Task Execute(string message, string subscription)
    {
        var notification = JsonConvert.DeserializeObject<NotificationEntity>(message);

        string emailPayload = string.Empty;
        string fromEmail = _configuration["SENDGRIDFROMEMAIL"];
        string toEmail = "jdrojass@unal.edu.co";

        if (subscription.Equals(ReceivedSubscriptionsConstants.TRANSACTION_COMPLETED))
        {
            notification.TransactionState = true;
            notification.Content = "Your transaction has been completed successfully.";
            
            string status = "Transaction Completed";
            emailPayload = CreateSendGridModel.Create(status, notification.Content, fromEmail, toEmail);

        }
        else
        {
            notification.TransactionState = false;
            notification.Content = "Your transaction has failed.";

            string status = "Transaction Failed";
            emailPayload = CreateSendGridModel.Create(status, notification.Content, fromEmail, toEmail);
        }

        await _sendGridEmailService.Execute(emailPayload);
        await ProcessDataBase(notification);
    }

    public async Task ProcessDataBase(NotificationEntity notification)
    {
        notification.Type = "email";
        await _db.AddAsync(notification);
    }
}
