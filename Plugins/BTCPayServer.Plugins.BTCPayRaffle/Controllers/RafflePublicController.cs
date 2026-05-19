#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using BTCPayServer.Plugins.BTCPayRaffle.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace BTCPayServer.Plugins.BTCPayRaffle.Controllers;

[Route("raffle")]
public class RafflePublicController : Controller
{
    private readonly RaffleService _raffle;
    private readonly InvoiceRepository _invoiceRepo;
    private readonly StoreRepository _storeRepo;
    private readonly UIInvoiceController _invoiceController;
    private readonly RaffleBuyerWalletTokenService _walletTokens;

    public RafflePublicController(
        RaffleService raffle,
        InvoiceRepository invoiceRepo,
        StoreRepository storeRepo,
        UIInvoiceController invoiceController,
        RaffleBuyerWalletTokenService walletTokens)
    {
        _raffle = raffle;
        _invoiceRepo = invoiceRepo;
        _storeRepo = storeRepo;
        _invoiceController = invoiceController;
        _walletTokens = walletTokens;
    }

    // ── Public raffle page ────────────────────────────────────────────────────

    [HttpGet("{raffleId}")]
    public async Task<IActionResult> View(Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.Status == RaffleStatus.Draft) return NotFound();
        return PublicRaffleView(raffle);
    }

    private IActionResult PublicRaffleView(Raffle raffle, BuyTicketsViewModel? buyForm = null)
    {
        var pageUrl = Url.Action(nameof(View), "RafflePublic", new { raffleId = raffle.Id }, Request.Scheme)!;
        return View(new RafflePublicViewModel
        {
            Raffle = raffle,
            QrCodeDataUrl = QrCodeService.GenerateQrBase64(pageUrl),
            TicketsSold = raffle.Tickets.Count,
            TicketPriceDisplay = RafflePricing.FormatTicketPrice(raffle),
            BuyForm = buyForm ?? new BuyTicketsViewModel()
        });
    }

    // ── Ticket purchase ───────────────────────────────────────────────────────

    [HttpPost("{raffleId}/buy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Buy(
        Guid raffleId,
        [Bind(Prefix = nameof(RafflePublicViewModel.BuyForm))] BuyTicketsViewModel buyForm,
        CancellationToken ct)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.Status != RaffleStatus.Open)
            return RedirectToAction(nameof(View), new { raffleId });

        if (!ModelState.IsValid)
            return PublicRaffleView(raffle, buyForm);

        if (raffle.MaxTickets.HasValue)
        {
            var remaining = raffle.MaxTickets.Value - raffle.Tickets.Count;
            if (buyForm.TicketCount > remaining)
            {
                ModelState.AddModelError($"{nameof(RafflePublicViewModel.BuyForm)}.{nameof(BuyTicketsViewModel.TicketCount)}",
                    $"Only {remaining} ticket(s) remaining");
                return PublicRaffleView(raffle, buyForm);
            }
        }

        var store = await _storeRepo.FindStore(raffle.StoreId);
        if (store is null) return Problem("Store not found");

        var totalAmount = raffle.TicketPrice * buyForm.TicketCount;
        var currency = raffle.TicketCurrency;

        // Raffle metadata stored in PosData — RaffleInvoiceWatcher reads it on payment confirmation
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var meta = new RaffleInvoiceMeta(raffleId, buyForm.TicketCount, buyForm.BuyerEmail, buyForm.BuyerName, baseUrl, raffle.Name);

        // entityManipulator gives access to the invoice ID before it's persisted,
        // so we can set the redirect URL to our per-invoice receipt page.
        var invoice = await _invoiceController.CreateInvoiceCoreRaw(
            new CreateInvoiceRequest
            {
                Amount = totalAmount,
                Currency = currency,
                Metadata = new InvoiceMetadata
                {
                    BuyerEmail = buyForm.BuyerEmail,
                    BuyerName = buyForm.BuyerName,
                    ItemCode = $"raffle-{raffleId}",
                    ItemDesc = $"{raffle.Name} — {buyForm.TicketCount}× ticket",
                    PosData = JObject.FromObject(meta)
                }.ToJObject()
            },
            store,
            Request.GetAbsoluteRoot(),
            cancellationToken: ct,
            entityManipulator: entity =>
            {
                // entity.Id is the final invoice ID — use it to build the receipt URL
                entity.RedirectURLTemplate = Url.Action(nameof(Receipt), "RafflePublic",
                    new { invoiceId = entity.Id }, Request.Scheme);
                entity.RedirectAutomatically = true;
            });

        var checkoutRoute = new { invoiceId = invoice.Id };
        var defaultPm = store.GetDefaultPaymentId();
        if (defaultPm is not null && invoice.GetPaymentPrompt(defaultPm) is not null)
            return RedirectToAction("Checkout", "UIInvoice", new { invoiceId = invoice.Id, paymentMethodId = defaultPm.ToString() });

        return RedirectToAction("Checkout", "UIInvoice", checkoutRoute);
    }

    // ── Ticket receipt ────────────────────────────────────────────────────────

    [HttpGet("receipt/{invoiceId}")]
    public async Task<IActionResult> Receipt(string invoiceId)
    {
        var (raffle, tickets) = await _raffle.GetReceiptAsync(invoiceId);
        if (raffle is null || tickets.Count == 0)
        {
            // Tickets may not be allocated yet if the payment was very recent
            return View("ReceiptPending", invoiceId);
        }

        var verifyUrl = Url.Action(nameof(Receipt), "RafflePublic",
            new { invoiceId }, Request.Scheme)!;

        var drawings = await _raffle.GetDrawingsAsync(raffle.Id);
        var winningNumbers = drawings.Select(d => d.WinningTicket.TicketNumber).ToList();

        var ticketQrCodes = tickets.ToDictionary(
            t => t.Id,
            t => QrCodeService.GenerateQrBase64(
                Url.Action(nameof(TicketVerify), "RafflePublic",
                    new { ticketId = t.Id }, Request.Scheme)!));

        var walletUrl = "";
        var buyerEmail = tickets[0].BuyerEmail;
        if (!string.IsNullOrEmpty(buyerEmail))
        {
            var (token, _) = _walletTokens.CreateToken(raffle.Id, buyerEmail);
            walletUrl = Url.Action(nameof(MyTickets), "RafflePublic",
                new { raffleId = raffle.Id, token }, Request.Scheme)!;
        }

        return View(new ReceiptViewModel
        {
            Raffle = raffle,
            Tickets = tickets,
            InvoiceId = invoiceId,
            VerifyUrl = verifyUrl,
            WalletUrl = walletUrl,
            QrCodeDataUrl = QrCodeService.GenerateQrBase64(verifyUrl),
            WinningNumbers = winningNumbers,
            TicketQrCodes = ticketQrCodes
        });
    }

    // ── Buyer wallet (all tickets for one email on this raffle) ─────────────────

    [HttpGet("{raffleId}/my")]
    public async Task<IActionResult> MyTickets(Guid raffleId, [FromQuery] string token)
    {
        if (!_walletTokens.TryValidate(token, raffleId, out var email, out _))
            return NotFound();

        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.Status == RaffleStatus.Draft) return NotFound();

        var tickets = await _raffle.GetTicketsByBuyerAsync(raffleId, email);
        if (tickets.Count == 0) return NotFound();

        var walletState = await _raffle.GetBuyerWalletStateAsync(raffleId, email);
        if (walletState is null) return NotFound();

        var displayName = tickets
            .Select(t => t.BuyerName)
            .LastOrDefault(n => !string.IsNullOrWhiteSpace(n));

        return View("~/Views/RafflePublic/MyTickets.cshtml", new BuyerWalletViewModel
        {
            Raffle = raffle,
            Tickets = tickets,
            WalletToken = token,
            StateUrl = Url.Action(nameof(MyTicketsState), "RafflePublic",
                new { raffleId, token }, Request.Scheme)!,
            DisplayName = RaffleBuyerDisplay.DisplayBuyerName(displayName),
            WinningNumbers = walletState.WinningNumbers,
            MyWinningNumbers = walletState.MyWinningNumbers,
            PurchaseCount = walletState.PurchaseCount,
            PendingDraw = walletState.PendingDraw is null ? null : new BuyerWalletPendingDrawResponse
            {
                DrawOrder = walletState.PendingDraw.DrawOrder,
                RevealAt = walletState.PendingDraw.RevealAt
            }
        });
    }

    [HttpGet("{raffleId}/my/state")]
    public async Task<IActionResult> MyTicketsState(Guid raffleId, [FromQuery] string token)
    {
        if (!_walletTokens.TryValidate(token, raffleId, out var email, out _))
            return NotFound();

        var state = await _raffle.GetBuyerWalletStateAsync(raffleId, email);
        if (state is null) return NotFound();

        var response = new BuyerWalletStateResponse
        {
            Status = state.Status,
            TicketNumbers = state.TicketNumbers,
            WinningNumbers = state.WinningNumbers,
            MyWinningNumbers = state.MyWinningNumbers,
            DrawingsCount = state.DrawingsCount,
            PurchaseCount = state.PurchaseCount,
            PendingDraw = state.PendingDraw is null ? null : new BuyerWalletPendingDrawResponse
            {
                DrawOrder = state.PendingDraw.DrawOrder,
                RevealAt = state.PendingDraw.RevealAt
            }
        };
        return new JsonResult(response)
        {
            SerializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            }
        };
    }

    // ── Ticket verification ───────────────────────────────────────────────────

    [HttpGet("ticket/{ticketId:guid}")]
    public async Task<IActionResult> TicketVerify(Guid ticketId)
    {
        var (ticket, raffle) = await _raffle.GetTicketWithDetailsAsync(ticketId);
        if (ticket is null || raffle is null) return NotFound();

        var winningTicketIds = raffle.Drawings.Select(d => d.WinningTicketId).ToHashSet();
        var isWinner = winningTicketIds.Contains(ticket.Id);
        var drawOrder = raffle.Drawings.FirstOrDefault(d => d.WinningTicketId == ticket.Id)?.DrawOrder;

        return View(new TicketVerifyViewModel
        {
            Ticket = ticket,
            Raffle = raffle,
            IsWinner = isWinner,
            DrawOrder = drawOrder,
            TotalDrawings = raffle.Drawings.Count
        });
    }
}
