using Bank.Transaction.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bank.Transaction.Api.Application.Database;

public interface IDatabaseService
{
    DbSet<TransactionEntity> Transaction { get; set; }

    Task<bool> SaveAsync();
}
