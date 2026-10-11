using Bank.Notification.Api.Application.Database;
using Bank.Notification.Api.Application.External.SendGridEmail;
using Bank.Notification.Api.Application.Features.Process;
using Bank.Notification.Api.External.SendGridEmail;
using Bank.Notification.Api.External.ServiceBusRecive;
using Bank.Notification.Api.Persistence.Database;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddScoped<IDatabaseService, DatabaseService>();
builder.Services.AddScoped<IProcessService, ProcessService>();

builder.Services.AddSingleton<ISendGridEmailService, SendGridEmailService>();


builder.Services.AddHostedService<ServiceBusReciveService>();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

var app = builder.Build();

app.Run();