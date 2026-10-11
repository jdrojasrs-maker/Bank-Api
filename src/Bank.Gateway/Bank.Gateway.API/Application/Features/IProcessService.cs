using Bank.Gateway.API.Application.Models;

namespace Bank.Gateway.API.Application.Features;

public interface IProcessService
{
    Task Execute(EndPointModel transactionEvent);
}
