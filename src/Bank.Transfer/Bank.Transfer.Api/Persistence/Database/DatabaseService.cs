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
    public DbSet<TransferRequestEntity> TransferRequest { get; set; }

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
        //modelBuilder.Entity<TransferEntity>().ToTable("Transfer");

        modelBuilder.Entity<TransferEntity>(entity =>
        {
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.HasIndex(x => x.CorrelationId).IsUnique().HasDatabaseName("IX_Transfer_CorrelationId");
        });

        modelBuilder.Entity<TransferRequestEntity>(entity =>
        {
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.Outcome).HasMaxLength(16);
            entity.Property(x => x.PayloadFingerprint).HasMaxLength(64);
            entity.HasIndex(x => x.CorrelationId).IsUnique().HasDatabaseName("IX_TransferRequest_CorrelationId");
        });
    }
}
