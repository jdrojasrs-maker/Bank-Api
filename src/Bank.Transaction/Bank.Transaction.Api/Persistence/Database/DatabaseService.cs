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

    public void ClearChanges()
    {
        ChangeTracker.Clear();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        //modelBuilder.Entity<TransactionEntity>().ToTable("Transaction");

        modelBuilder.Entity<TransactionEntity>(entity =>
        {
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.HasIndex(x => x.CorrelationId).IsUnique().HasDatabaseName("IX_Transaction_CorrelationId");
        });
    }
}
