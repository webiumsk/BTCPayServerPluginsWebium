#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;

namespace BTCPayServer.Plugins.BTCPayRaffle.ViewModels;

public class RaffleAdminListViewModel
{
    public string StoreId { get; set; } = "";
    public List<Raffle> Raffles { get; set; } = new();
}

public class CreateEditRaffleViewModel
{
    public Guid? RaffleId { get; set; }
    public RaffleStatus? Status { get; set; }
    public int TicketsSold { get; set; }

    [Required(ErrorMessage = "Name is required")]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(2000)]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Currency is required")]
    [MaxLength(10)]
    [Display(Name = "Ticket currency")]
    public string TicketCurrency { get; set; } = "SATS";

    [Required(ErrorMessage = "Ticket price is required")]
    [Range(0.00000001, double.MaxValue, ErrorMessage = "Price must be positive")]
    [Display(Name = "Ticket price")]
    public decimal TicketPrice { get; set; } = 21_000;

    [Range(1, int.MaxValue)]
    [Display(Name = "Max tickets (leave blank for unlimited)")]
    public int? MaxTickets { get; set; }

    public bool CanEditPricing { get; set; } = true;
    public bool CanEditMaxTickets { get; set; } = true;
    public List<string> AvailableCurrencies { get; set; } = new();
}

public class RaffleManageViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public string StoreId { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public string QrCodeDataUrl { get; set; } = "";
    public string TicketPriceDisplay { get; set; } = "";
    public int TotalTicketsSold => Raffle.Tickets.Count;
    public bool CanDelete => Raffle.Status is RaffleStatus.Draft or RaffleStatus.Completed;
    public bool CanAddManualTickets => Raffle.Status is RaffleStatus.Open or RaffleStatus.Closed
        && !Raffle.Drawings.Any();
}

public class ManualTicketsViewModel
{
    [Required]
    [Range(1, 100)]
    [Display(Name = "Number of tickets")]
    public int Count { get; set; } = 1;

    [Required]
    [EmailAddress]
    [Display(Name = "Buyer email")]
    public string BuyerEmail { get; set; } = "";

    [MaxLength(200)]
    [Display(Name = "Buyer name (optional)")]
    public string? BuyerName { get; set; }
}

public class DrawViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public string StoreId { get; set; } = "";
    public List<RaffleDrawing> Drawings { get; set; } = new();
    public int EligibleTicketsCount { get; set; }
    public bool CanDrawMore => EligibleTicketsCount > 0;
    public bool CanUndoLastDraw => Raffle.Status == RaffleStatus.Drawing && Drawings.Count > 0;
}

/// <summary>Token-authenticated public presenter (Satflux / event screen).</summary>
public class PresenterDrawViewModel : DrawViewModel
{
    public string PresenterToken { get; set; } = "";
    public string DrawActionUrl { get; set; } = "";
    public string DrawStateUrl { get; set; } = "";
}

public class RafflePublicViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public string QrCodeDataUrl { get; set; } = "";
    public int TicketsSold { get; set; }
    public string TicketPriceDisplay { get; set; } = "";
    public BuyTicketsViewModel BuyForm { get; set; } = new();
}

public class BuyTicketsViewModel
{
    [Required]
    [Range(1, 100, ErrorMessage = "Please select between 1 and 100 tickets")]
    [Display(Name = "Number of tickets")]
    public int TicketCount { get; set; } = 1;

    [Required]
    [EmailAddress]
    [Display(Name = "Your email (required — we send your ticket numbers here)")]
    public string BuyerEmail { get; set; } = "";

    [MaxLength(100)]
    [Display(Name = "Display name (optional)")]
    public string? BuyerName { get; set; }
}

public class ReceiptViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public List<RaffleTicket> Tickets { get; set; } = new();
    public string InvoiceId { get; set; } = "";
    public string VerifyUrl { get; set; } = "";
    public string WalletUrl { get; set; } = "";
    public string QrCodeDataUrl { get; set; } = "";
    public List<int> WinningNumbers { get; set; } = new();
    public Dictionary<Guid, string> TicketQrCodes { get; set; } = new();
}

public class BuyerWalletViewModel
{
    public Raffle Raffle { get; set; } = null!;
    public List<RaffleTicket> Tickets { get; set; } = new();
    public string StateUrl { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<int> WinningNumbers { get; set; } = new();
    public List<int> MyWinningNumbers { get; set; } = new();
    public int PurchaseCount { get; set; }
    public BuyerWalletPendingDrawResponse? PendingDraw { get; set; }
}

public class BuyerWalletPendingDrawResponse
{
    public int DrawOrder { get; set; }
    public DateTimeOffset RevealAt { get; set; }
}

public class BuyerWalletStateResponse
{
    public string Status { get; set; } = "";
    public List<int> TicketNumbers { get; set; } = new();
    public List<int> WinningNumbers { get; set; } = new();
    public List<int> MyWinningNumbers { get; set; } = new();
    public int DrawingsCount { get; set; }
    public int PurchaseCount { get; set; }
    public BuyerWalletPendingDrawResponse? PendingDraw { get; set; }
}

public class TicketVerifyViewModel
{
    public RaffleTicket Ticket { get; set; } = null!;
    public Raffle Raffle { get; set; } = null!;
    public bool IsWinner { get; set; }
    public int? DrawOrder { get; set; }
    public int TotalDrawings { get; set; }
}

public class CreateRaffleRequest
{
    [Required]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? TicketCurrency { get; set; }
    public decimal? TicketPrice { get; set; }
    public long? TicketPriceSats { get; set; }
    public int? MaxTickets { get; set; }
}

public class UpdateRaffleRequest
{
    [Required]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? TicketCurrency { get; set; }
    public decimal? TicketPrice { get; set; }
    public long? TicketPriceSats { get; set; }
    public int? MaxTickets { get; set; }
}

public class AddManualTicketsRequest
{
    [Required, Range(1, 100)]
    public int Count { get; set; } = 1;

    [Required]
    [EmailAddress]
    public string BuyerEmail { get; set; } = "";

    [MaxLength(200)]
    public string? BuyerName { get; set; }
}

public class DrawResultResponse
{
    public int DrawOrder { get; set; }
    public int WinningTicketNumber { get; set; }
    public string? WinnerName { get; set; }
    public string? WinnerEmail { get; set; }
    public DateTimeOffset DrawnAt { get; set; }
}

public class PresenterTokenResponse
{
    public string Token { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string PresenterUrl { get; set; } = "";
}

public class DrawStateResponse
{
    public string Status { get; set; } = "";
    public int TotalTickets { get; set; }
    public int EligibleTicketsRemaining { get; set; }
    public int DrawingsCount { get; set; }
    public bool CanDraw { get; set; }
    public bool CanUndoLastDraw { get; set; }
}
