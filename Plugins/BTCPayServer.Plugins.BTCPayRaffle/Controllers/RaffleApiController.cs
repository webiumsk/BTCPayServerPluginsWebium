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

/// <summary>
/// BTCPayRaffle Greenfield API.
/// Base path: /api/v1/stores/{storeId}/raffle
/// Authentication: BTCPay API key with Modify Store permission (header: X-Api-Key)
/// </summary>
[ApiController]
[Route("api/v1/stores/{storeId}/raffle")]
[Authorize(Policy = Policies.CanModifyStoreSettings,
           AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
public class RaffleApiController : ControllerBase
{
    private readonly RaffleService _raffle;

    public RaffleApiController(RaffleService raffle) => _raffle = raffle;

    /// <summary>List all raffles in the store.</summary>
    [HttpGet]
    public async Task<IActionResult> List(string storeId) =>
        Ok((await _raffle.GetRafflesForStoreAsync(storeId)).Select(MapRaffle));

    /// <summary>Create a new raffle.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(string storeId, [FromBody] CreateRaffleRequest req)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var r = await _raffle.CreateRaffleAsync(
            storeId, req.Name, req.Description, req.TicketPriceSats, req.MaxTickets);
        return Created($"/api/v1/stores/{storeId}/raffle/{r.Id}", MapRaffle(r));
    }

    /// <summary>Get a single raffle.</summary>
    [HttpGet("{raffleId}")]
    public async Task<IActionResult> Get(string storeId, Guid raffleId)
    {
        var r = await GetOwned(storeId, raffleId);
        return r is null ? NotFound() : Ok(MapRaffle(r));
    }

    /// <summary>Open ticket sales.</summary>
    [HttpPost("{raffleId}/open")]
    public async Task<IActionResult> Open(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try { await _raffle.OpenRaffleAsync(raffleId); } catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        return Ok(new { status = "Open" });
    }

    /// <summary>Close ticket sales.</summary>
    [HttpPost("{raffleId}/close")]
    public async Task<IActionResult> Close(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try { await _raffle.CloseRaffleSalesAsync(raffleId); } catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        return Ok(new { status = "Closed" });
    }

    /// <summary>Draw the next prize. Returns the winning ticket number.</summary>
    [HttpPost("{raffleId}/draw")]
    public async Task<IActionResult> DrawNext(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        try
        {
            var (d, w) = await _raffle.DrawNextPrizeAsync(raffleId);
            return Ok(new DrawResultResponse
            {
                DrawOrder = d.DrawOrder,
                WinningTicketNumber = w.TicketNumber,
                WinnerName = w.BuyerName,
                WinnerEmail = w.BuyerEmail,
                DrawnAt = d.DrawnAt
            });
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    /// <summary>Mark the raffle as completed.</summary>
    [HttpPost("{raffleId}/complete")]
    public async Task<IActionResult> Complete(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        await _raffle.CompleteRaffleAsync(raffleId);
        return Ok(new { status = "Completed" });
    }

    /// <summary>List all sold tickets.</summary>
    [HttpGet("{raffleId}/tickets")]
    public async Task<IActionResult> Tickets(string storeId, Guid raffleId)
    {
        var r = await GetOwned(storeId, raffleId);
        if (r is null) return NotFound();
        return Ok(r.Tickets.OrderBy(t => t.TicketNumber).Select(t => new
        {
            t.TicketNumber, t.BuyerName, t.BuyerEmail, t.AllocatedAt,
            receiptUrl = $"/raffle/receipt/{t.InvoiceId}"
        }));
    }

    /// <summary>List all draw results.</summary>
    [HttpGet("{raffleId}/drawings")]
    public async Task<IActionResult> Drawings(string storeId, Guid raffleId)
    {
        if (await GetOwned(storeId, raffleId) is null) return NotFound();
        var drawings = await _raffle.GetDrawingsAsync(raffleId);
        return Ok(drawings.Select(d => new DrawResultResponse
        {
            DrawOrder = d.DrawOrder,
            WinningTicketNumber = d.WinningTicket.TicketNumber,
            WinnerName = d.WinningTicket.BuyerName,
            WinnerEmail = d.WinningTicket.BuyerEmail,
            DrawnAt = d.DrawnAt
        }));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Raffle?> GetOwned(string storeId, Guid raffleId)
    {
        var r = await _raffle.GetRaffleAsync(raffleId);
        return r?.StoreId == storeId ? r : null;
    }

    private static object MapRaffle(Raffle r) => new
    {
        id = r.Id, r.Name, r.Description, r.StoreId,
        r.TicketPriceSats, r.MaxTickets,
        status = r.Status.ToString(),
        ticketsSold = r.Tickets.Count,
        r.CreatedAt, r.OpenedAt, r.ClosedAt, r.CompletedAt
    };
}
