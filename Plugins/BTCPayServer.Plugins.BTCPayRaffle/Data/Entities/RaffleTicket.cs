#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;

public class RaffleTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RaffleId { get; set; }
    public Raffle Raffle { get; set; } = null!;

    public int TicketNumber { get; set; }

    [Required, MaxLength(100)]
    public string InvoiceId { get; set; } = "";

    public bool IsManual { get; set; }

    [MaxLength(200)]
    public string? BuyerEmail { get; set; }

    [MaxLength(200)]
    public string? BuyerName { get; set; }

    public DateTimeOffset AllocatedAt { get; set; } = DateTimeOffset.UtcNow;
}
