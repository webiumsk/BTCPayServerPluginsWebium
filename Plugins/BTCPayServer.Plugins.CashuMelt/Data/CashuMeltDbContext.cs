using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.CashuMelt.Data;

public class CashuMeltDbContext : DbContext
{
    public CashuMeltDbContext(DbContextOptions<CashuMeltDbContext> options)
        : base(options)
    {
    }

    public DbSet<CashuMeltStoreSettings> CashuMeltStoreSettings { get; set; }
    public DbSet<CashuMeltPaymentRequest> CashuMeltPaymentRequests { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.CashuMelt");

        modelBuilder.Entity<CashuMeltStoreSettings>(entity =>
        {
            entity.HasIndex(e => e.StoreId).IsUnique();
            entity.Property(e => e.MaxMeltFeeReservePercentOfMinted).HasPrecision(5, 2);
        });

        modelBuilder.Entity<CashuMeltPaymentRequest>(entity =>
        {
            entity.HasIndex(e => e.QuoteId).IsUnique();
            entity.HasIndex(e => e.InvoiceId);
            entity.HasIndex(e => e.StoreId);
        });
    }
}
