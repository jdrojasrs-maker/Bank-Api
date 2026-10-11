using Bank.Balance.Api.Application.Database;
using Bank.Balance.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bank.Balance.Api.Persistence.Database;

public class DatabaseService: DbContext, IDatabaseService
{
    public DatabaseService(DbContextOptions<DatabaseService> options) : base(options)
    {
    }
    public DbSet<BalanceEntity> Balance { get; set; }

    public async Task<bool> SaveAsync()
    {
        return await base.SaveChangesAsync() > 0;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        //modelBuilder.Entity<BalanceEntity>().ToTable("Balance");
    }
}
