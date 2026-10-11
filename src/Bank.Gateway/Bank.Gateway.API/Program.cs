using Bank.Gateway.API.Api.Endpoint;
using Bank.Gateway.API.Application.External.ServiceBusSender;
using Bank.Gateway.API.Application.Features;
using Bank.Gateway.API.External.ServiceBusSender;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IServiceBusSenderService, ServiceBusSenderService>();
builder.Services.AddSingleton<IProcessService, ProcessService>();

var app = builder.Build();
ApiGatewayEndPoint.GatewayEndPoints(app);
app.Run();
