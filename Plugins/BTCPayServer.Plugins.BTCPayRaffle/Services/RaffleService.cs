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

    public async Task<Raffle> CreateRaffleAsync(
        string storeId,
        string name,
        string? description,
        string ticketCurrency,
        decimal ticketPrice,
        int? maxTickets = null)
    {
        await using var ctx = _db.CreateContext();
        var raffle = new Raffle
        {
            StoreId = storeId,
            Name = name,
            Description = description,
            MaxTickets = maxTickets
        };
        RafflePricing.ApplyPricing(raffle, ticketCurrency, ticketPrice);
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

    /// <summary>Greenfield / Satflux: only while <see cref="RaffleStatus.Draft"/>.</summary>
    public async Task UpdateDraftRaffleAsync(
        Guid id,
        string name,
        string? description,
        string ticketCurrency,
        decimal ticketPrice,
        int? maxTickets)
    {
        await using var ctx = _db.CreateContext();
        var raffle = await ctx.Raffles.FindAsync(id)
            ?? throw new InvalidOperationException("Raffle not found");

        if (raffle.Status != RaffleStatus.Draft)
            throw new InvalidOperationException("Only Draft raffles can be updated via the API");

        raffle.Name = name;
        raffle.Description = description;
        RafflePricing.ApplyPricing(raffle, ticketCurrency, ticketPrice);
        raffle.MaxTickets = maxTickets;
        await ctx.SaveChangesAsync();
    }

    /// <summary>BTCPay store UI: broader edit rules by status.</summary>
    public async Task UpdateRaffleAsync(
        Guid id,
        string name,
        string? description,
        string? ticketCurrency,
        decimal? ticketPrice,
        int? maxTickets)
    {
        await using var ctx = _db.CreateContext();
        var raffle = await ctx.Raffles.Include(r => r.Tickets).FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new InvalidOperationException("Raffle not found");

        var ticketsSold = raffle.Tickets.Count;
        var canEditPricing = raffle.Status switch
        {
            RaffleStatus.Draft => true,
            RaffleStatus.Open => ticketsSold == 0,
            _ => false
        };
        var canEditMaxTickets = canEditPricing;

        if (raffle.Status is RaffleStatus.Completed)
            throw new InvalidOperationException("Completed raffles cannot be edited");

        raffle.Name = name;
        raffle.Description = description;

        if (ticketCurrency is not null && ticketPrice is not null)
        {
            if (!canEditPricing)
                throw new InvalidOperationException(
                    "Ticket price and currency can only be changed while the raffle is in Draft, or Open with no tickets sold");
            RafflePricing.ApplyPricing(raffle, ticketCurrency, ticketPrice.Value);
        }

        if (maxTickets != raffle.MaxTickets)
        {
            if (!canEditMaxTickets)
                throw new InvalidOperationException(
                    "Max tickets can only be changed while the raffle is in Draft, or Open with no tickets sold");
            if (maxTickets.HasValue && maxTickets.Value < ticketsSold)
                throw new InvalidOperationException(
                    $"Max tickets cannot be less than tickets already sold ({ticketsSold})");
            raffle.MaxTickets = maxTickets;
        }

        await ctx.SaveChangesAsync();
    }

    public async Task DeleteRaffleAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var raffle = await ctx.Raffles.FindAsync(id)
            ?? throw new InvalidOperationException("Raffle not found");

        if (raffle.Status is not (RaffleStatus.Draft or RaffleStatus.Completed))
            throw new InvalidOperationException(
                "Only Draft or Completed raffles can be deleted. Close sales and complete the raffle first, or delete while still in Draft.");

        ctx.Raffles.Remove(raffle);
        await ctx.SaveChangesAsync();
        _logger.LogInformation("Deleted raffle {RaffleId} (status was {Status})", id, raffle.Status);
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

    public async Task<(List<RaffleTicket> Tickets, bool IsNew)> AllocateTicketsAsync(
        string invoiceId, Guid raffleId, int count, string? buyerEmail, string? buyerName)
    {
        if (RaffleTicketIds.IsManual(invoiceId))
            throw new InvalidOperationException("Invalid invoice id for paid allocation");

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

        var tickets = CreateTicketEntities(raffle, count, invoiceId, buyerEmail, buyerName, isManual: false);
        ctx.RaffleTickets.AddRange(tickets);
        await ctx.SaveChangesAsync();

        _logger.LogInformation(
            "Allocated tickets {First}-{Last} (raffle={RaffleId}, invoice={InvoiceId})",
            tickets[0].TicketNumber, tickets[^1].TicketNumber, raffleId, invoiceId);

        return (tickets, true);
    }

    public async Task<List<RaffleTicket>> AddManualTicketsAsync(
        Guid raffleId, int count, string? buyerEmail, string? buyerName)
    {
        if (count < 1 || count > 100)
            throw new ArgumentException("Count must be between 1 and 100");

        await using var ctx = _db.CreateContext();
        var raffle = await ctx.Raffles
            .Include(r => r.Tickets)
            .Include(r => r.Drawings)
            .FirstOrDefaultAsync(r => r.Id == raffleId)
            ?? throw new InvalidOperationException("Raffle not found");

        if (raffle.Status is not (RaffleStatus.Open or RaffleStatus.Closed))
            throw new InvalidOperationException(
                "Manual tickets can only be added while sales are open or after sales are closed (before drawing)");

        if (raffle.Drawings.Count > 0)
            throw new InvalidOperationException("Cannot add manual tickets after drawing has started");

        EnsureCapacity(raffle, count);

        var invoiceId = RaffleTicketIds.NewManual();
        var tickets = CreateTicketEntities(raffle, count, invoiceId, buyerEmail, buyerName, isManual: true);
        ctx.RaffleTickets.AddRange(tickets);
        await ctx.SaveChangesAsync();

        _logger.LogInformation(
            "Added {Count} manual ticket(s) {First}-{Last} (raffle={RaffleId})",
            count, tickets[0].TicketNumber, tickets[^1].TicketNumber, raffleId);

        return tickets;
    }

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

    public async Task<RaffleDrawing> UndoLastDrawingAsync(Guid raffleId)
    {
        await using var ctx = _db.CreateContext();
        var raffle = await ctx.Raffles
            .Include(r => r.Drawings)
            .FirstOrDefaultAsync(r => r.Id == raffleId)
            ?? throw new InvalidOperationException("Raffle not found");

        if (raffle.Status != RaffleStatus.Drawing)
            throw new InvalidOperationException("Undo is only available while the raffle is in Drawing status");

        var last = await ctx.RaffleDrawings
            .Where(d => d.RaffleId == raffleId)
            .OrderByDescending(d => d.DrawOrder)
            .FirstOrDefaultAsync();

        if (last is null)
            throw new InvalidOperationException("No drawings to undo");

        ctx.RaffleDrawings.Remove(last);
        if (raffle.Drawings.Count == 1)
            raffle.Status = RaffleStatus.Closed;

        await ctx.SaveChangesAsync();
        _logger.LogInformation("Undid draw order {DrawOrder} for raffle {RaffleId}", last.DrawOrder, raffleId);
        return last;
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

    public async Task<List<RaffleDrawing>> GetDrawingsAsync(Guid raffleId)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RaffleDrawings
            .Include(d => d.WinningTicket)
            .Where(d => d.RaffleId == raffleId)
            .OrderBy(d => d.DrawOrder)
            .ToListAsync();
    }

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
        if (RaffleTicketIds.IsManual(invoiceId))
            return (null, new List<RaffleTicket>());

        await using var ctx = _db.CreateContext();
        var tickets = await ctx.RaffleTickets
            .Where(t => t.InvoiceId == invoiceId)
            .OrderBy(t => t.TicketNumber)
            .ToListAsync();
        if (tickets.Count == 0) return (null, new List<RaffleTicket>());
        var raffle = await ctx.Raffles.FindAsync(tickets[0].RaffleId);
        return (raffle, tickets);
    }

    public async Task<List<RaffleTicket>> GetTicketsByBuyerAsync(Guid raffleId, string normalizedEmail)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RaffleTickets
            .Where(t => t.RaffleId == raffleId
                && t.BuyerEmail != null
                && t.BuyerEmail == normalizedEmail)
            .OrderBy(t => t.TicketNumber)
            .ToListAsync();
    }

    public async Task<BuyerWalletState?> GetBuyerWalletStateAsync(Guid raffleId, string normalizedEmail)
    {
        await using var ctx = _db.CreateContext();
        var raffle = await ctx.Raffles
            .Include(r => r.Drawings).ThenInclude(d => d.WinningTicket)
            .FirstOrDefaultAsync(r => r.Id == raffleId);
        if (raffle is null) return null;

        var tickets = await ctx.RaffleTickets
            .Where(t => t.RaffleId == raffleId
                && t.BuyerEmail != null
                && t.BuyerEmail == normalizedEmail)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        var myNumbers = tickets.Select(t => t.TicketNumber).OrderBy(n => n).ToList();
        var orderedDrawings = raffle.Drawings.OrderBy(d => d.DrawOrder).ToList();
        var revealedDrawings = orderedDrawings
            .Where(d => RaffleDrawReveal.IsRevealed(d.DrawnAt, now))
            .ToList();
        var pendingDrawing = orderedDrawings
            .LastOrDefault(d => !RaffleDrawReveal.IsRevealed(d.DrawnAt, now));

        var winningNumbers = revealedDrawings
            .Select(d => d.WinningTicket.TicketNumber)
            .ToList();
        var myWinning = myNumbers.Intersect(winningNumbers).OrderBy(n => n).ToList();

        BuyerWalletPendingDraw? pendingDraw = null;
        if (pendingDrawing is not null)
        {
            pendingDraw = new BuyerWalletPendingDraw
            {
                DrawOrder = pendingDrawing.DrawOrder,
                RevealAt = RaffleDrawReveal.RevealAt(pendingDrawing.DrawnAt)
            };
        }

        return new BuyerWalletState
        {
            Status = raffle.Status.ToString(),
            TicketNumbers = myNumbers,
            WinningNumbers = winningNumbers,
            MyWinningNumbers = myWinning,
            DrawingsCount = raffle.Drawings.Count,
            PurchaseCount = tickets.Select(t => t.InvoiceId).Distinct().Count(),
            PendingDraw = pendingDraw
        };
    }

    private static List<RaffleTicket> CreateTicketEntities(
        Raffle raffle,
        int count,
        string invoiceId,
        string? buyerEmail,
        string? buyerName,
        bool isManual)
    {
        EnsureCapacity(raffle, count);

        var nextNumber = raffle.Tickets.Count == 0
            ? 1
            : raffle.Tickets.Max(t => t.TicketNumber) + 1;

        return Enumerable.Range(0, count).Select(i => new RaffleTicket
        {
            RaffleId = raffle.Id,
            TicketNumber = nextNumber + i,
            InvoiceId = invoiceId,
            IsManual = isManual,
            BuyerEmail = RaffleBuyerEmail.Normalize(buyerEmail),
            BuyerName = string.IsNullOrWhiteSpace(buyerName) ? null : buyerName.Trim()
        }).ToList();
    }

    private static void EnsureCapacity(Raffle raffle, int count)
    {
        if (!raffle.MaxTickets.HasValue) return;
        var remaining = raffle.MaxTickets.Value - raffle.Tickets.Count;
        if (count > remaining)
            throw new InvalidOperationException($"Only {remaining} ticket(s) remaining");
    }
}
