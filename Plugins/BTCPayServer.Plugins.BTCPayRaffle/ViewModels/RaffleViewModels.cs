#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;

namespace BTCPayServer.Plugins.BTCPayRaffle.ViewModels;

// ── Admin ViewModels ─────────────────────────────────────────────────────────

public class RaffleAdminListViewModel
{
    public string StoreId { get; set; } = "";
    public List<Raffle> Raffles { get; set; } = new();
}

public class CreateEditRaffleViewModel
{
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(2000)]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Ticket price is required")]
    [Range(1, long.MaxValue, ErrorMessage = "Price must be at least 1 sat")]
    [Display(Name = "Ticket price (sats)")]
    public long TicketPriceSats { get; set; } = 21_000;

    [Range(1, int.MaxValue)]
    [Display(Name = "Max tickets (leave blank for unlimited)")]
    public int? MaxTickets { get; set; }
}

public class RaffleManageViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public string StoreId { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public string QrCodeDataUrl { get; set; } = "";
    public int TotalTicketsSold => Raffle.Tickets.Count;
    public long TotalRevenueSats => Raffle.Tickets.Count * Raffle.TicketPriceSats;
}

public class DrawViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public string StoreId { get; set; } = "";
    public List<RaffleDrawing> Drawings { get; set; } = new();
    public int EligibleTicketsCount { get; set; }
    public bool CanDrawMore => EligibleTicketsCount > 0;
}

// ── Public ViewModels ─────────────────────────────────────────────────────────

public class RafflePublicViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public string QrCodeDataUrl { get; set; } = "";
    public int TicketsSold { get; set; }
}

public class BuyTicketsViewModel
{
    [Required]
    [Range(1, 100, ErrorMessage = "Please select between 1 and 100 tickets")]
    [Display(Name = "Number of tickets")]
    public int TicketCount { get; set; } = 1;

    [EmailAddress]
    [Display(Name = "Your email (optional — we'll send your ticket numbers)")]
    public string? BuyerEmail { get; set; }

    [MaxLength(100)]
    [Display(Name = "Your name (optional)")]
    public string? BuyerName { get; set; }
}

public class ReceiptViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public List<RaffleTicket> Tickets { get; set; } = new();
    public string InvoiceId { get; set; } = "";
    public string VerifyUrl { get; set; } = "";
    public string QrCodeDataUrl { get; set; } = "";
    public List<int> WinningNumbers { get; set; } = new();
    /// <summary>Per-ticket QR code data URIs keyed by ticket Guid.</summary>
    public Dictionary<Guid, string> TicketQrCodes { get; set; } = new();
}

public class TicketVerifyViewModel
{
    public RaffleTicket Ticket { get; set; } = null!;
    public Raffle Raffle { get; set; } = null!;
    public bool IsWinner { get; set; }
    public int? DrawOrder { get; set; }
    public int TotalDrawings { get; set; }
}

// ── API Models ───────────────────────────────────────────────────────────────

public class CreateRaffleRequest
{
    [Required]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    [Required, Range(1, long.MaxValue)]
    public long TicketPriceSats { get; set; }
    public int? MaxTickets { get; set; }
}

public class DrawResultResponse
{
    public int DrawOrder { get; set; }
    public int WinningTicketNumber { get; set; }
    public string? WinnerName { get; set; }
    public string? WinnerEmail { get; set; }
    public System.DateTimeOffset DrawnAt { get; set; }
}
