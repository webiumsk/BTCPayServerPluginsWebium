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

[ApiController]
[Route("api/v1/stores/{storeId}/raffle")]
[Authorize(Policy = Policies.CanModifyStoreSettings,
           AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
public class RaffleApiController : ControllerBase
{
    private readonly RaffleService _raffle;
    private readonly RafflePresenterTokenService _presenterTokens;

    public RaffleApiController(RaffleService raffle, RafflePresenterTokenService presenterTokens)
    {
        _raffle = raffle;
        _presenterTokens = presenterTokens;
    }

    [HttpGet]
    public async Task<IActionResult> List(string storeId) =>
        Ok((await _raffle.GetRafflesForStoreAsync(storeId)).Select(MapRaffle));

    [HttpPost]
    public async Task<IActionResult> Create(string storeId, [FromBody] CreateRaffleRequest req)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        try
        {
            var (currency, price) = ResolveCreatePricing(req);
            var r = await _raffle.CreateRaffleAsync(
                storeId, req.Name, req.Description, currency, price, req.MaxTickets);
            return Created($"/api/v1/stores/{storeId}/raffle/{r.Id}", MapRaffle(r));
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("{raffleId}")]
    public async Task<IActionResult> Get(string storeId, Guid raffleId)
    {
        var r = await GetOwned(storeId, raffleId);
        return r is null ? NotFound() : Ok(MapRaffle(r));
    }

    [HttpPut("{raffleId}")]
    public async Task<IActionResult> Update(string storeId, Guid raffleId, [FromBody] UpdateRaffleRequest req)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try
        {
            var (currency, price) = ResolveUpdatePricing(req);
            await _raffle.UpdateDraftRaffleAsync(
                raffleId, req.Name, req.Description, currency, price, req.MaxTickets);
            var updated = await _raffle.GetRaffleAsync(raffleId);
            return Ok(MapRaffle(updated!));
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("{raffleId}/presenter-token")]
    public async Task<IActionResult> CreatePresenterToken(string storeId, Guid raffleId)
    {
        var raffle = await GetOwned(storeId, raffleId);
        if (raffle is null) return NotFound();

        var (token, expiresAt) = _presenterTokens.CreateToken(raffleId, storeId);
        var presenterUrl = Url.Action(
            "Present", "RafflePresent",
            new { raffleId, token },
            Request.Scheme)!;

        return Ok(new PresenterTokenResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
            PresenterUrl = presenterUrl
        });
    }

    [HttpGet("{raffleId}/draw-state")]
    public async Task<IActionResult> DrawState(string storeId, Guid raffleId)
    {
        var raffle = await GetOwned(storeId, raffleId);
        if (raffle is null) return NotFound();
        var state = RaffleDrawStateBuilder.FromRaffle(raffle);
        return Ok(MapDrawState(state));
    }

    [HttpDelete("{raffleId}")]
    public async Task<IActionResult> Delete(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try
        {
            await _raffle.DeleteRaffleAsync(raffleId);
            return Ok(new { deleted = true });
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("{raffleId}/open")]
    public async Task<IActionResult> Open(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try { await _raffle.OpenRaffleAsync(raffleId); } catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        return Ok(new { status = "Open" });
    }

    [HttpPost("{raffleId}/close")]
    public async Task<IActionResult> Close(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try { await _raffle.CloseRaffleSalesAsync(raffleId); } catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        return Ok(new { status = "Closed" });
    }

    [HttpPost("{raffleId}/draw")]
    public async Task<IActionResult> DrawNext(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try
        {
            var (d, w) = await _raffle.DrawNextPrizeAsync(raffleId);
            return Ok(MapDrawResult(d, w));
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpDelete("{raffleId}/drawings/last")]
    public async Task<IActionResult> UndoLastDraw(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try
        {
            var removed = await _raffle.UndoLastDrawingAsync(raffleId);
            var raffle = await _raffle.GetRaffleAsync(raffleId);
            return Ok(new
            {
                undoneDrawOrder = removed.DrawOrder,
                status = raffle!.Status.ToString()
            });
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("{raffleId}/complete")]
    public async Task<IActionResult> Complete(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        await _raffle.CompleteRaffleAsync(raffleId);
        return Ok(new { status = "Completed" });
    }

    [HttpPost("{raffleId}/tickets/manual")]
    public async Task<IActionResult> AddManualTickets(
        string storeId, Guid raffleId, [FromBody] AddManualTicketsRequest req)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try
        {
            var tickets = await _raffle.AddManualTicketsAsync(
                raffleId, req.Count, req.BuyerEmail, req.BuyerName);
            return Ok(tickets.Select(MapTicket));
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("{raffleId}/tickets")]
    public async Task<IActionResult> Tickets(string storeId, Guid raffleId)
    {
        var r = await GetOwned(storeId, raffleId);
        if (r is null) return NotFound();
        return Ok(r.Tickets.OrderBy(t => t.TicketNumber).Select(MapTicket));
    }

    [HttpGet("{raffleId}/drawings")]
    public async Task<IActionResult> Drawings(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        var drawings = await _raffle.GetDrawingsAsync(raffleId);
        return Ok(drawings.Select(d => MapDrawResult(d, d.WinningTicket)));
    }

    private async Task<Raffle?> GetOwned(string storeId, Guid raffleId)
    {
        var r = await _raffle.GetRaffleAsync(raffleId);
        return r?.StoreId == storeId ? r : null;
    }

    private static (string Currency, decimal Price) ResolveCreatePricing(CreateRaffleRequest req) =>
        ResolvePricing(req.TicketCurrency, req.TicketPrice, req.TicketPriceSats);

    private static (string Currency, decimal Price) ResolveUpdatePricing(UpdateRaffleRequest req) =>
        ResolvePricing(req.TicketCurrency, req.TicketPrice, req.TicketPriceSats);

    private static (string Currency, decimal Price) ResolvePricing(
        string? ticketCurrency, decimal? ticketPrice, long? ticketPriceSats)
    {
        if (ticketPrice is { } price && !string.IsNullOrWhiteSpace(ticketCurrency))
            return (ticketCurrency, price);
        if (ticketPriceSats is { } sats)
            return (RafflePricing.SatsCurrency, sats);
        if (ticketPrice is { } p2)
            return (ticketCurrency ?? RafflePricing.SatsCurrency, p2);
        throw new ArgumentException("Provide ticketPrice and ticketCurrency, or ticketPriceSats");
    }

    private static DrawStateResponse MapDrawState(RaffleDrawState state) => new()
    {
        Status = state.Status,
        TotalTickets = state.TotalTickets,
        EligibleTicketsRemaining = state.EligibleTicketsRemaining,
        DrawingsCount = state.DrawingsCount,
        CanDraw = state.CanDraw,
        CanUndoLastDraw = state.CanUndoLastDraw
    };

    private static object MapRaffle(Raffle r) => new
    {
        id = r.Id,
        r.Name,
        r.Description,
        r.StoreId,
        ticketCurrency = r.TicketCurrency,
        ticketPrice = r.TicketPrice,
        ticketPriceSats = RafflePricing.DisplayTicketPriceSats(r),
        r.MaxTickets,
        status = r.Status.ToString(),
        ticketsSold = r.Tickets.Count,
        r.CreatedAt,
        r.OpenedAt,
        r.ClosedAt,
        r.CompletedAt
    };

    private static object MapTicket(RaffleTicket t) => new
    {
        t.TicketNumber,
        buyerName = RaffleBuyerDisplay.DisplayBuyerName(t.BuyerName),
        buyerEmail = RaffleBuyerDisplay.MaskEmail(t.BuyerEmail),
        t.AllocatedAt,
        t.IsManual,
        receiptUrl = t.IsManual ? null : $"/raffle/receipt/{t.InvoiceId}"
    };

    private static DrawResultResponse MapDrawResult(RaffleDrawing d, RaffleTicket w) => new()
    {
        DrawOrder = d.DrawOrder,
        WinningTicketNumber = w.TicketNumber,
        WinnerName = RaffleBuyerDisplay.DisplayBuyerName(w.BuyerName),
        WinnerEmail = RaffleBuyerDisplay.MaskEmail(w.BuyerEmail),
        DrawnAt = d.DrawnAt
    };
}
