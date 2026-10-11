using Bank.Transaction.Api.Application.Database;
using Bank.Transaction.Api.Application.Features.Process;
using Bank.Transaction.Api.Application.Handlers;
using Bank.Transaction.Api.External.ServiceBusRecive;
using Bank.Transaction.Api.Persistence.Database;
using Bank.Transaction.API.Application.External.ServiceBusSender;
using Bank.Transaction.API.External.ServiceBusSender;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<DatabaseService>( options => 
    options.UseSqlServer(builder.Configuration["TRANSACTIONSQLDBCONSTR"]));
builder.Services.AddScoped<IDatabaseService, DatabaseService>();
builder.Services.AddScoped<IProcessService, ProcessService>();
builder.Services.AddSingleton<IServiceBusSenderService, ServiceBusSenderService>();

builder.Services.AddHostedService<ServiceBusReciveService>();

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessHandler).Assembly));

var app = builder.Build();

app.Run();