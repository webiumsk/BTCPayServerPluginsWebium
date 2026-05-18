#nullable enable
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.BTCPayRaffle.Data;

public class RaffleDbContext : DbContext
{
    public RaffleDbContext(DbContextOptions<RaffleDbContext> options) : base(options) { }

    public DbSet<Raffle> Raffles => Set<Raffle>();
    public DbSet<RaffleTicket> RaffleTickets => Set<RaffleTicket>();
    public DbSet<RaffleDrawing> RaffleDrawings => Set<RaffleDrawing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(RaffleDbContextFactory.Schema);

        modelBuilder.Entity<Raffle>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.StoreId);
            e.HasMany(r => r.Tickets)
                .WithOne(t => t.Raffle)
                .HasForeignKey(t => t.RaffleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Drawings)
                .WithOne(d => d.Raffle)
                .HasForeignKey(d => d.RaffleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RaffleTicket>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.InvoiceId);
            e.HasIndex(t => new { t.RaffleId, t.TicketNumber }).IsUnique();
        });

        modelBuilder.Entity<RaffleDrawing>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasOne(d => d.WinningTicket)
                .WithMany()
                .HasForeignKey(d => d.WinningTicketId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(d => new { d.RaffleId, d.DrawOrder }).IsUnique();
        });
    }
}
