#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using BTCPayServer.Plugins.BTCPayRaffle.ViewModels;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BTCPayRaffle.Controllers;

[Route("stores/{storeId}/plugins/raffle")]
[Authorize(Policy = Policies.CanModifyStoreSettings,
           AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIRaffleController : Controller
{
    private readonly RaffleService _raffle;
    private readonly StoreRepository _storeRepo;
    private readonly CurrencyNameTable _currencies;
    private readonly RaffleTicketEmailService _ticketEmail;

    public UIRaffleController(
        RaffleService raffle,
        StoreRepository storeRepo,
        CurrencyNameTable currencies,
        RaffleTicketEmailService ticketEmail)
    {
        _raffle = raffle;
        _storeRepo = storeRepo;
        _currencies = currencies;
        _ticketEmail = ticketEmail;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string storeId)
    {
        var raffles = await _raffle.GetRafflesForStoreAsync(storeId);
        return View(new RaffleAdminListViewModel { StoreId = storeId, Raffles = raffles });
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create(string storeId) =>
        View(await BuildEditViewModelAsync(storeId, null));

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string storeId, CreateEditRaffleViewModel vm)
    {
        if (!await ValidateCurrencyAsync(storeId, vm)) return View(vm);
        if (!ModelState.IsValid) return View(vm);
        try
        {
            var raffle = await _raffle.CreateRaffleAsync(
                storeId, vm.Name, vm.Description, vm.TicketCurrency, vm.TicketPrice, vm.MaxTickets);
            TempData[WellKnownTempData.SuccessMessage] = "Raffle created successfully";
            return RedirectToAction(nameof(Manage), new { storeId, raffleId = raffle.Id });
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(vm);
        }
    }

    [HttpGet("{raffleId}/edit")]
    public async Task<IActionResult> Edit(string storeId, Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.StoreId != storeId) return NotFound();
        if (raffle.Status == RaffleStatus.Completed)
            return RedirectToAction(nameof(Manage), new { storeId, raffleId });
        return View(await BuildEditViewModelAsync(storeId, raffle));
    }

    [HttpPost("{raffleId}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string storeId, Guid raffleId, CreateEditRaffleViewModel vm)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.StoreId != storeId) return NotFound();
        vm = await BuildEditViewModelAsync(storeId, raffle, vm);
        if (!await ValidateCurrencyAsync(storeId, vm)) return View(vm);
        if (!ModelState.IsValid) return View(vm);
        try
        {
            await _raffle.UpdateRaffleAsync(
                raffleId,
                vm.Name,
                vm.Description,
                vm.CanEditPricing ? vm.TicketCurrency : null,
                vm.CanEditPricing ? vm.TicketPrice : null,
                vm.CanEditMaxTickets ? vm.MaxTickets : raffle.MaxTickets);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(vm);
        }
        TempData[WellKnownTempData.SuccessMessage] = "Raffle updated";
        return RedirectToAction(nameof(Manage), new { storeId, raffleId });
    }

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
            QrCodeDataUrl = QrCodeService.GenerateQrBase64(publicUrl),
            TicketPriceDisplay = RafflePricing.FormatTicketPrice(raffle)
        });
    }

    [HttpPost("{raffleId}/manual-tickets")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddManualTickets(
        string storeId, Guid raffleId, ManualTicketsViewModel vm)
    {
        if (!ModelState.IsValid)
            return RedirectToAction(nameof(Manage), new { storeId, raffleId });
        try
        {
            var tickets = await _raffle.AddManualTicketsAsync(raffleId, vm.Count, vm.BuyerEmail, vm.BuyerName);
            var raffle = await _raffle.GetRaffleAsync(raffleId);
            if (raffle is not null)
            {
                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                await _ticketEmail.SendTicketsEmailAsync(
                    raffleId, raffle.Name, vm.BuyerEmail, vm.BuyerName, tickets, baseUrl,
                    manualAllocation: true);
            }
            TempData[WellKnownTempData.SuccessMessage] = $"Added {vm.Count} manual ticket(s)";
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = ex.Message;
        }
        return RedirectToAction(nameof(Manage), new { storeId, raffleId });
    }

    [HttpGet("{raffleId}/delete")]
    public async Task<IActionResult> Delete(string storeId, Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.StoreId != storeId) return NotFound();
        if (raffle.Status is not (RaffleStatus.Draft or RaffleStatus.Completed))
            return RedirectToAction(nameof(Manage), new { storeId, raffleId });

        return View("Confirm", new ConfirmModel(
            "Delete raffle",
            $"The raffle <strong>{System.Net.WebUtility.HtmlEncode(raffle.Name)}</strong> and all tickets and draw history will be permanently deleted.",
            "Delete"));
    }

    [HttpPost("{raffleId}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePost(string storeId, Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null || raffle.StoreId != storeId) return NotFound();
        try
        {
            await _raffle.DeleteRaffleAsync(raffleId);
            TempData[WellKnownTempData.SuccessMessage] = "Raffle deleted";
        }
        catch (InvalidOperationException ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = ex.Message;
            return RedirectToAction(nameof(Manage), new { storeId, raffleId });
        }
        return RedirectToAction(nameof(Index), new { storeId });
    }

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
                winnerName = RaffleBuyerDisplay.DisplayBuyerName(winner.BuyerName),
                winnerEmail = RaffleBuyerDisplay.MaskEmail(winner.BuyerEmail),
                drawnAt = drawing.DrawnAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return Json(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("{raffleId}/undo-draw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoLastDraw(string storeId, Guid raffleId)
    {
        try
        {
            await _raffle.UndoLastDrawingAsync(raffleId);
            TempData[WellKnownTempData.SuccessMessage] = "Last draw undone — winner is eligible again";
        }
        catch (InvalidOperationException ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = ex.Message;
        }
        return RedirectToAction(nameof(Draw), new { storeId, raffleId });
    }

    [HttpPost("{raffleId}/complete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(string storeId, Guid raffleId)
    {
        await _raffle.CompleteRaffleAsync(raffleId);
        TempData[WellKnownTempData.SuccessMessage] = "Raffle completed";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    private async Task<CreateEditRaffleViewModel> BuildEditViewModelAsync(
        string storeId, Raffle? raffle, CreateEditRaffleViewModel? posted = null)
    {
        var store = await _storeRepo.FindStore(storeId);
        var defaultCurrency = store?.GetStoreBlob().DefaultCurrency ?? "USD";
        var currencies = _currencies.Currencies.Select(c => c.Code).OrderBy(c => c).ToList();
        if (!currencies.Contains(RafflePricing.SatsCurrency, StringComparer.OrdinalIgnoreCase))
            currencies.Insert(0, RafflePricing.SatsCurrency);

        var ticketsSold = raffle?.Tickets.Count ?? 0;
        var status = raffle?.Status ?? RaffleStatus.Draft;
        var canEditPricing = status == RaffleStatus.Draft
            || (status == RaffleStatus.Open && ticketsSold == 0);

        var vm = posted ?? new CreateEditRaffleViewModel();
        if (posted is null && raffle is not null)
        {
            vm.RaffleId = raffle.Id;
            vm.Status = raffle.Status;
            vm.TicketsSold = ticketsSold;
            vm.Name = raffle.Name;
            vm.Description = raffle.Description;
            vm.TicketCurrency = raffle.TicketCurrency;
            vm.TicketPrice = raffle.TicketPrice;
            vm.MaxTickets = raffle.MaxTickets;
        }
        else if (posted is null)
        {
            vm.TicketCurrency = defaultCurrency;
            vm.TicketPrice = 10;
        }

        vm.AvailableCurrencies = currencies;
        vm.CanEditPricing = canEditPricing;
        vm.CanEditMaxTickets = canEditPricing;
        vm.TicketsSold = ticketsSold;
        vm.Status = status;
        return vm;
    }

    private async Task<bool> ValidateCurrencyAsync(string storeId, CreateEditRaffleViewModel vm)
    {
        vm.AvailableCurrencies = (await BuildEditViewModelAsync(storeId, null)).AvailableCurrencies;
        if (_currencies.GetCurrencyData(vm.TicketCurrency, false) is null)
        {
            ModelState.AddModelError(nameof(vm.TicketCurrency), "Invalid currency");
            return false;
        }
        if (RafflePricing.NormalizeCurrency(vm.TicketCurrency) == RafflePricing.SatsCurrency
            && vm.TicketPrice != decimal.Truncate(vm.TicketPrice))
        {
            ModelState.AddModelError(nameof(vm.TicketPrice), "SATS price must be a whole number");
            return false;
        }
        return true;
    }
}
