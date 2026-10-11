using Bank.Gateway.API.Application.Features;
using Bank.Gateway.API.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace Bank.Gateway.API.Api.Endpoint;

public static class ApiGatewayEndPoint
{
    public static void GatewayEndPoints(WebApplication app)
    {
        app.MapPost("/api-gateway",async ([FromBody] EndPointModel modelRequest,
            [FromServices] IProcessService processService) =>
        {
            await processService.Execute(modelRequest);
            return modelRequest;
        });
    }
}
