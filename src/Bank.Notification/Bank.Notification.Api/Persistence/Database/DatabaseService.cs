using Bank.Notification.Api.Application.Database;
using Bank.Notification.Api.Domain.Entities;
using Microsoft.Azure.Cosmos;

namespace Bank.Notification.Api.Persistence.Database;

public class DatabaseService: IDatabaseService
{
    private readonly CosmosClient cosmosClient;
    private readonly Container container;

    public DatabaseService(IConfiguration configuration)
    {
        string connectionString = configuration["NOTIFICATIONDBCONSTR"];
        string dataBase = configuration["NOTIFICATIONDBNAME"];
        string containerName = configuration["NOTIFICATIONDBCONTAINER"];

        cosmosClient = new CosmosClient(connectionString);
        container = cosmosClient.GetContainer(dataBase, containerName);
    }

    public async Task<bool> AddAsync(NotificationEntity notification)
    {
        notification.Id = Guid.NewGuid().ToString();
        notification.NotificationDate = DateTime.UtcNow;

        var response = await container.CreateItemAsync(notification, new PartitionKey(notification.CorrelationId));
        return response.StatusCode == System.Net.HttpStatusCode.Created;
    }

    public async Task<List<NotificationEntity>> GetAllAsync(string id, int customerId)
    {
       var query = container.GetItemQueryIterator<NotificationEntity>(
            new QueryDefinition("SELECT * FROM c "));
        List<NotificationEntity> notifications = new List<NotificationEntity>();
        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync();
            notifications.AddRange(response);
        }
        return notifications;
    }
}
