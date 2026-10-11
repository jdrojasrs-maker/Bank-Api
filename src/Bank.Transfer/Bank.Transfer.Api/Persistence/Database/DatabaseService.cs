using Bank.Transfer.Api.Application.Database;
using Bank.Transfer.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bank.Transfer.Api.Persistence.Database;

public class DatabaseService: DbContext, IDatabaseService
{
    public DatabaseService(DbContextOptions<DatabaseService> options) : base(options)
    {
    }

    public DbSet<TransferEntity> Transfer { get; set; }

    public async Task<bool> SaveAsync()
    {
        return await base.SaveChangesAsync() > 0;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        //modelBuilder.Entity<TransferEntity>().ToTable("Transfer");
    }
}
