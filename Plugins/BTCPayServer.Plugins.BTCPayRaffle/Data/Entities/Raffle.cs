#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;

public enum RaffleStatus
{
    Draft,      // created but not yet published
    Open,       // ticket sales in progress
    Closed,     // sales ended, drawing not started
    Drawing,    // prizes being drawn
    Completed   // all prizes awarded
}

public class Raffle
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(2000)]
    public string? Description { get; set; }

    [Required, MaxLength(100)]
    public string StoreId { get; set; } = "";

    /// <summary>Legacy satoshi price; kept in sync when <see cref="TicketCurrency"/> is SATS.</summary>
    public long TicketPriceSats { get; set; }

    [Required, MaxLength(10)]
    public string TicketCurrency { get; set; } = "SATS";

    public decimal TicketPrice { get; set; }

    /// <summary>Maximum number of tickets; null means unlimited.</summary>
    public int? MaxTickets { get; set; }

    public RaffleStatus Status { get; set; } = RaffleStatus.Draft;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<RaffleTicket> Tickets { get; set; } = new();
    public List<RaffleDrawing> Drawings { get; set; } = new();
}
