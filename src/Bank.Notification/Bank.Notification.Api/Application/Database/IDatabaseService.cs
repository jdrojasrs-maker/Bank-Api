using Bank.Notification.Api.Domain.Entities;

namespace Bank.Notification.Api.Application.Database;

public interface IDatabaseService
{
    Task<bool> AddAsync(NotificationEntity notification);
    Task<List<NotificationEntity>> GetAllAsync(string id, int customerId);
}
