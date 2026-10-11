using Azure.Messaging.ServiceBus;
using Bank.Balance.Api.Application.Database;
using Bank.Balance.Api.Application.External.ServiceBusSender;
using Bank.Balance.Api.Application.Features.Process;
using Bank.Balance.Api.External.ServiceBusRecive;
using Bank.Balance.Api.External.ServiceBusSender;
using Bank.Balance.Api.Persistence.Database;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddDbContext<DatabaseService>(options =>
    options.UseSqlServer(builder.Configuration["BALANCESQLDBCONSTR"]));

builder.Services.AddScoped<IDatabaseService, DatabaseService>();
builder.Services.AddScoped<IProcessService, ProcessService>();
builder.Services.AddSingleton<IServiceBusSenderService, ServiceBusSenderService>();


builder.Services.AddHostedService<ServiceBusReciveService>();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

var app = builder.Build();

app.Run();
