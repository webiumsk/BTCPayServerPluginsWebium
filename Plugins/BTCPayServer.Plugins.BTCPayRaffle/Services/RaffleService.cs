#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCPayRaffle.Data;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public class RaffleService
{
    private readonly RaffleDbContextFactory _db;
    private readonly ILogger<RaffleService> _logger;

    public RaffleService(RaffleDbContextFactory db, ILogger<RaffleService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Raffle CRUD ──────────────────────────────────────────────────────────

    public async Task<Raffle> CreateRaffleAsync(
        string storeId, string name, string? description, long priceSats, int? maxTickets = null)
    {
        await using var ctx = _db.CreateContext();
        var raffle = new Raffle
        {
            StoreId = storeId,
            Name = name,
            Description = description,
            TicketPriceSats = priceSats,
            MaxTickets = maxTickets
        };
        ctx.Raffles.Add(raffle);
        await ctx.SaveChangesAsync();
        return raffle;
    }

    public async Task<Raffle?> GetRaffleAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.Raffles
            .Include(r => r.Tickets)
            .Include(r => r.Drawings).ThenInclude(d => d.WinningTicket)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<List<Raffle>> GetRafflesForStoreAsync(string storeId)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.Raffles
            .Where(r => r.StoreId == storeId)
            .Include(r => r.Tickets)
            .Include(r => r.Drawings)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task UpdateRaffleAsync(
        Guid id, string name, string? description, long priceSats, int? maxTickets)
    {
        await using var ctx = _db.CreateContext();
        var raffle = await ctx.Raffles.FindAsync(id)
            ?? throw new InvalidOperationException("Raffle not found");
        if (raffle.Status != RaffleStatus.Draft)
            throw new InvalidOperationException("Only Draft raffles can be edited");
        raffle.Name = name;
        raffle.Description = description;
        raffle.TicketPriceSats = priceSats;
        raffle.MaxTickets = maxTickets;
        await ctx.SaveChangesAsync();
    }

    public async Task OpenRaffleAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var r = await ctx.Raffles.FindAsync(id) ?? throw new InvalidOperationException("Raffle not found");
        if (r.Status != RaffleStatus.Draft)
            throw new InvalidOperationException("Only Draft raffles can be opened");
        r.Status = RaffleStatus.Open;
        r.OpenedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    public async Task CloseRaffleSalesAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var r = await ctx.Raffles.FindAsync(id) ?? throw new InvalidOperationException("Raffle not found");
        if (r.Status != RaffleStatus.Open)
            throw new InvalidOperationException("Only Open raffles can be closed");
        r.Status = RaffleStatus.Closed;
        r.ClosedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    public async Task CompleteRaffleAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var r = await ctx.Raffles.FindAsync(id) ?? throw new InvalidOperationException("Raffle not found");
        r.Status = RaffleStatus.Completed;
        r.CompletedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    // ── Ticket Allocation ────────────────────────────────────────────────────

    /// <summary>
    /// Allocates ticket numbers after a payment is confirmed.
    /// Idempotent: a second call for the same invoiceId returns the existing tickets with isNew=false.
    /// </summary>
    public async Task<(List<RaffleTicket> Tickets, bool IsNew)> AllocateTicketsAsync(
        string invoiceId, Guid raffleId, int count, string? buyerEmail, string? buyerName)
    {
        await using var ctx = _db.CreateContext();

        var existing = await ctx.RaffleTickets
            .Where(t => t.InvoiceId == invoiceId)
            .ToListAsync();
        if (existing.Count > 0) return (existing, false);

        var raffle = await ctx.Raffles.Include(r => r.Tickets)
            .FirstOrDefaultAsync(r => r.Id == raffleId)
            ?? throw new InvalidOperationException("Raffle not found");

        if (raffle.Status != RaffleStatus.Open)
            throw new InvalidOperationException("Raffle is not accepting ticket purchases");

        var nextNumber = raffle.Tickets.Count == 0
            ? 1
            : raffle.Tickets.Max(t => t.TicketNumber) + 1;

        var tickets = Enumerable.Range(0, count).Select(i => new RaffleTicket
        {
            RaffleId = raffleId,
            TicketNumber = nextNumber + i,
            InvoiceId = invoiceId,
            BuyerEmail = buyerEmail,
            BuyerName = buyerName
        }).ToList();

        ctx.RaffleTickets.AddRange(tickets);
        await ctx.SaveChangesAsync();

        _logger.LogInformation(
            "Allocated tickets {First}-{Last} (raffle={RaffleId}, invoice={InvoiceId})",
            tickets[0].TicketNumber, tickets[^1].TicketNumber, raffleId, invoiceId);

        return (tickets, true);
    }

    public async Task<(RaffleTicket? Ticket, Raffle? Raffle)> GetTicketWithDetailsAsync(Guid ticketId)
    {
        await using var ctx = _db.CreateContext();
        var ticket = await ctx.RaffleTickets
            .Include(t => t.Raffle)
                .ThenInclude(r => r.Drawings)
            .FirstOrDefaultAsync(t => t.Id == ticketId);
        return (ticket, ticket?.Raffle);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the next prize using a cryptographically secure RNG.
    /// The first call draws the last prize; the final call draws the grand prize.
    /// </summary>
    public async Task<(RaffleDrawing Drawing, RaffleTicket Winner)> DrawNextPrizeAsync(Guid raffleId)
    {
        await using var ctx = _db.CreateContext();
        var raffle = await ctx.Raffles
            .Include(r => r.Tickets)
            .Include(r => r.Drawings)
            .FirstOrDefaultAsync(r => r.Id == raffleId)
            ?? throw new InvalidOperationException("Raffle not found");

        if (raffle.Status is not (RaffleStatus.Closed or RaffleStatus.Drawing))
            throw new InvalidOperationException("Raffle must be Closed before drawing prizes");

        raffle.Status = RaffleStatus.Drawing;

        var drawnIds = raffle.Drawings.Select(d => d.WinningTicketId).ToHashSet();
        var eligible = raffle.Tickets.Where(t => !drawnIds.Contains(t.Id)).ToList();
        if (eligible.Count == 0)
            throw new InvalidOperationException("No eligible tickets remaining");

        var winner = eligible[RandomNumberGenerator.GetInt32(eligible.Count)];
        var drawing = new RaffleDrawing
        {
            RaffleId = raffleId,
            DrawOrder = raffle.Drawings.Count + 1,
            WinningTicketId = winner.Id,
            WinningTicket = winner
        };

        ctx.RaffleDrawings.Add(drawing);
        await ctx.SaveChangesAsync();
        return (drawing, winner);
    }

    public async Task<List<RaffleDrawing>> GetDrawingsAsync(Guid raffleId)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RaffleDrawings
            .Include(d => d.WinningTicket)
            .Where(d => d.RaffleId == raffleId)
            .OrderBy(d => d.DrawOrder)
            .ToListAsync();
    }

    // ── Receipt Lookup ───────────────────────────────────────────────────────

    public async Task<List<RaffleTicket>> GetTicketsByInvoiceAsync(string invoiceId)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RaffleTickets
            .Where(t => t.InvoiceId == invoiceId)
            .OrderBy(t => t.TicketNumber)
            .ToListAsync();
    }

    public async Task<(Raffle? Raffle, List<RaffleTicket> Tickets)> GetReceiptAsync(string invoiceId)
    {
        await using var ctx = _db.CreateContext();
        var tickets = await ctx.RaffleTickets
            .Where(t => t.InvoiceId == invoiceId)
            .OrderBy(t => t.TicketNumber)
            .ToListAsync();
        if (tickets.Count == 0) return (null, new());
        var raffle = await ctx.Raffles.FindAsync(tickets[0].RaffleId);
        return (raffle, tickets);
    }
}
