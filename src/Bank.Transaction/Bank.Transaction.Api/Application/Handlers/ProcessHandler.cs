using Bank.Transaction.Api.Application.Features.Process;
using Bank.Transaction.Api.Domain.Events;
using MediatR;

namespace Bank.Transaction.Api.Application.Handlers;

public class ProcessHandler : INotificationHandler<ProcessEvents>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ProcessHandler(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }
    public async Task Handle(ProcessEvents notification, CancellationToken cancellationToken)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var _processService = scope.ServiceProvider.GetRequiredService<IProcessService>();
            await _processService.Execute(notification.Message, notification.Subscription);
        }
            
    }
}
