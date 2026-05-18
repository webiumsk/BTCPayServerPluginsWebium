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
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BTCPayRaffle.Controllers;

[Route("raffle")]
public class RafflePublicController : Controller
{
    private readonly RaffleService _raffle;
    private readonly InvoiceRepository _invoiceRepo;
    private readonly StoreRepository _storeRepo;
    private readonly UIInvoiceController _invoiceController;

    public RafflePublicController(
        RaffleService raffle,
        InvoiceRepository invoiceRepo,
        StoreRepository storeRepo,
        UIInvoiceController invoiceController)
    {
        _raffle = raffle;
        _invoiceRepo = invoiceRepo;
        _storeRepo = storeRepo;
        _invoiceController = invoiceController;
    }

    // ── Public raffle page ────────────────────────────────────────────────────

    [HttpGet("{raffleId}")]
    public async Task<IActionResult> View(Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.Status == RaffleStatus.Draft) return NotFound();

        var pageUrl = Url.Action(nameof(View), "RafflePublic", new { raffleId }, Request.Scheme)!;
        return View(new RafflePublicViewModel
        {
            Raffle = raffle,
            QrCodeDataUrl = QrCodeService.GenerateQrBase64(pageUrl),
            TicketsSold = raffle.Tickets.Count
        });
    }

    // ── Ticket purchase ───────────────────────────────────────────────────────

    [HttpPost("{raffleId}/buy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Buy(Guid raffleId, BuyTicketsViewModel vm, CancellationToken ct)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.Status != RaffleStatus.Open)
            return RedirectToAction(nameof(View), new { raffleId });

        if (!ModelState.IsValid)
            return View(vm);

        if (raffle.MaxTickets.HasValue)
        {
            var remaining = raffle.MaxTickets.Value - raffle.Tickets.Count;
            if (vm.TicketCount > remaining)
            {
                ModelState.AddModelError(nameof(vm.TicketCount),
                    $"Only {remaining} ticket(s) remaining");
                return View(vm);
            }
        }

        var store = await _storeRepo.FindStore(raffle.StoreId);
        if (store is null) return Problem("Store not found");

        var totalSats = raffle.TicketPriceSats * vm.TicketCount;

        // Raffle metadata stored in PosData — RaffleInvoiceWatcher reads it on payment confirmation
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var meta = new RaffleInvoiceMeta(raffleId, vm.TicketCount, vm.BuyerEmail, vm.BuyerName, baseUrl, raffle.Name);

        // entityManipulator gives access to the invoice ID before it's persisted,
        // so we can set the redirect URL to our per-invoice receipt page.
        var invoice = await _invoiceController.CreateInvoiceCoreRaw(
            new CreateInvoiceRequest
            {
                Amount = totalSats,
                Currency = "SATS",
                Metadata = new InvoiceMetadata
                {
                    BuyerEmail = vm.BuyerEmail,
                    BuyerName = vm.BuyerName,
                    ItemCode = $"raffle-{raffleId}",
                    ItemDesc = $"{raffle.Name} — {vm.TicketCount}× ticket",
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

        return View(new ReceiptViewModel
        {
            Raffle = raffle,
            Tickets = tickets,
            InvoiceId = invoiceId,
            VerifyUrl = verifyUrl,
            QrCodeDataUrl = QrCodeService.GenerateQrBase64(verifyUrl),
            WinningNumbers = winningNumbers,
            TicketQrCodes = ticketQrCodes
        });
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
