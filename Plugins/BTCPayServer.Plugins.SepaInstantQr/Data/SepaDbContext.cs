using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.SepaInstantQr.Data;

public class SepaDbContext : DbContext
{
    public SepaDbContext(DbContextOptions<SepaDbContext> options)
        : base(options)
    {
    }

    public DbSet<SepaStoreSettings> SepaStoreSettings { get; set; }
    public DbSet<SepaPaymentRequest> SepaPaymentRequests { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.SepaInstantQr");

        modelBuilder.Entity<SepaStoreSettings>(entity =>
        {
            entity.Property(e => e.AmountTolerance).HasPrecision(18, 2);
            entity.HasIndex(e => e.FioTokenFingerprint).IsUnique()
                .HasFilter("\"FioTokenFingerprint\" IS NOT NULL");
        });

        modelBuilder.Entity<SepaPaymentRequest>(entity =>
        {
            entity.HasIndex(e => e.InvoiceId);
            entity.HasIndex(e => e.StoreId);
            entity.HasIndex(e => e.State);
            entity.HasIndex(e => e.DedupKey).IsUnique().HasFilter("\"DedupKey\" IS NOT NULL");
            entity.Property(e => e.AmountDue).HasPrecision(18, 2);
        });
    }
}
