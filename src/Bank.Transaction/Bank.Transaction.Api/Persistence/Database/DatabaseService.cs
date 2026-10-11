using Bank.Transaction.Api.Application.Database;
using Bank.Transaction.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bank.Transaction.Api.Persistence.Database;

public class DatabaseService: DbContext, IDatabaseService
{
    public DatabaseService(DbContextOptions<DatabaseService> options) : base(options)
    {
    }

    public DbSet<TransactionEntity> Transaction { get; set; }

    public async Task<bool> SaveAsync()
    {
        return await base.SaveChangesAsync() > 0;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        //modelBuilder.Entity<TransactionEntity>().ToTable("Transaction");
    }
}
