#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;

public class RaffleTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RaffleId { get; set; }
    public Raffle Raffle { get; set; } = null!;

    /// <summary>Sequential ticket number shown to the buyer, starting from 1.</summary>
    public int TicketNumber { get; set; }

    /// <summary>BTCPay invoice ID — used to look up tickets after payment.</summary>
    [Required, MaxLength(50)]
    public string InvoiceId { get; set; } = "";

    [MaxLength(200)]
    public string? BuyerEmail { get; set; }

    [MaxLength(200)]
    public string? BuyerName { get; set; }

    public DateTimeOffset AllocatedAt { get; set; } = DateTimeOffset.UtcNow;
}
