#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using BTCPayServer.Plugins.BTCPayRaffle.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BTCPayRaffle.Controllers;

[Route("stores/{storeId}/plugins/raffle")]
[Authorize(Policy = Policies.CanModifyStoreSettings,
           AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIRaffleController : Controller
{
    private readonly RaffleService _raffle;

    public UIRaffleController(RaffleService raffle) => _raffle = raffle;

    // ── Index ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Index(string storeId)
    {
        var raffles = await _raffle.GetRafflesForStoreAsync(storeId);
        return View(new RaffleAdminListViewModel { StoreId = storeId, Raffles = raffles });
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [HttpGet("create")]
    public IActionResult Create(string storeId) => View(new CreateEditRaffleViewModel());

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string storeId, CreateEditRaffleViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var raffle = await _raffle.CreateRaffleAsync(
            storeId, vm.Name, vm.Description, vm.TicketPriceSats, vm.MaxTickets);
        TempData[WellKnownTempData.SuccessMessage] = "Raffle created successfully";
        return RedirectToAction(nameof(Manage), new { storeId, raffleId = raffle.Id });
    }

    // ── Edit ──────────────────────────────────────────────────────────────────

    [HttpGet("{raffleId}/edit")]
    public async Task<IActionResult> Edit(string storeId, Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.StoreId != storeId) return NotFound();
        return View(new CreateEditRaffleViewModel
        {
            Name = raffle.Name,
            Description = raffle.Description,
            TicketPriceSats = raffle.TicketPriceSats,
            MaxTickets = raffle.MaxTickets
        });
    }

    [HttpPost("{raffleId}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string storeId, Guid raffleId, CreateEditRaffleViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        try
        {
            await _raffle.UpdateRaffleAsync(raffleId, vm.Name, vm.Description,
                vm.TicketPriceSats, vm.MaxTickets);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(vm);
        }
        TempData[WellKnownTempData.SuccessMessage] = "Raffle updated";
        return RedirectToAction(nameof(Manage), new { storeId, raffleId });
    }

    // ── Manage ────────────────────────────────────────────────────────────────

    [HttpGet("{raffleId}")]
    public async Task<IActionResult> Manage(string storeId, Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.StoreId != storeId) return NotFound();

        var publicUrl = Url.Action("View", "RafflePublic", new { raffleId }, Request.Scheme)!;
        return View(new RaffleManageViewModel
        {
            Raffle = raffle,
            StoreId = storeId,
            PublicUrl = publicUrl,
            QrCodeDataUrl = QrCodeService.GenerateQrBase64(publicUrl)
        });
    }

    // ── Status Transitions ────────────────────────────────────────────────────

    [HttpPost("{raffleId}/open")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Open(string storeId, Guid raffleId)
    {
        await _raffle.OpenRaffleAsync(raffleId);
        TempData[WellKnownTempData.SuccessMessage] = "Raffle published — ticket sales are now open";
        return RedirectToAction(nameof(Manage), new { storeId, raffleId });
    }

    [HttpPost("{raffleId}/close")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseSales(string storeId, Guid raffleId)
    {
        await _raffle.CloseRaffleSalesAsync(raffleId);
        TempData[WellKnownTempData.SuccessMessage] = "Ticket sales closed — you can now start the draw";
        return RedirectToAction(nameof(Manage), new { storeId, raffleId });
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    [HttpGet("{raffleId}/draw")]
    public async Task<IActionResult> Draw(string storeId, Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.StoreId != storeId) return NotFound();
        if (raffle.Status is RaffleStatus.Draft or RaffleStatus.Open)
            return RedirectToAction(nameof(Manage), new { storeId, raffleId });

        var drawings = await _raffle.GetDrawingsAsync(raffleId);
        var drawnIds = drawings.Select(d => d.WinningTicketId).ToHashSet();
        var eligible = raffle.Tickets.Count(t => !drawnIds.Contains(t.Id));

        return View(new DrawViewModel
        {
            Raffle = raffle,
            StoreId = storeId,
            Drawings = drawings,
            EligibleTicketsCount = eligible
        });
    }

    /// <summary>
    /// AJAX endpoint — server picks the winner, client runs the slot-machine animation,
    /// then reveals the result after ~5 seconds.
    /// </summary>
    [HttpPost("{raffleId}/draw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DrawNext(string storeId, Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.StoreId != storeId) return NotFound();

        try
        {
            var (drawing, winner) = await _raffle.DrawNextPrizeAsync(raffleId);
            return Json(new
            {
                success = true,
                drawOrder = drawing.DrawOrder,
                ticketNumber = winner.TicketNumber,
                winnerName = winner.BuyerName,
                winnerEmail = winner.BuyerEmail,
                drawnAt = drawing.DrawnAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("{raffleId}/complete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(string storeId, Guid raffleId)
    {
        await _raffle.CompleteRaffleAsync(raffleId);
        TempData[WellKnownTempData.SuccessMessage] = "Raffle completed";
        return RedirectToAction(nameof(Index), new { storeId });
    }
}
