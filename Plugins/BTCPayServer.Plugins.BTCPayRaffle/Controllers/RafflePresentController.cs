#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using BTCPayServer.Plugins.BTCPayRaffle.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BTCPayRaffle.Controllers;

/// <summary>
/// Public presenter UI for live draws. Requires a token from Greenfield
/// <c>POST .../presenter-token</c> — not BTCPay user session.
/// </summary>
[Route("raffle")]
public class RafflePresentController : Controller
{
    private readonly RaffleService _raffle;
    private readonly RafflePresenterTokenService _tokens;

    public RafflePresentController(RaffleService raffle, RafflePresenterTokenService tokens)
    {
        _raffle = raffle;
        _tokens = tokens;
    }

    [HttpGet("{raffleId}/present")]
    public async Task<IActionResult> Present(Guid raffleId, [FromQuery] string token)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null) return NotFound();
        if (!TryValidateToken(token, raffle, out var bad)) return bad;

        if (raffle.Status is RaffleStatus.Draft or RaffleStatus.Open)
            return View("PresentUnavailable", raffle);

        var drawings = await _raffle.GetDrawingsAsync(raffleId);
        var drawnIds = drawings.Select(d => d.WinningTicketId).ToHashSet();
        var eligible = raffle.Tickets.Count(t => !drawnIds.Contains(t.Id));

        var vm = new PresenterDrawViewModel
        {
            Raffle = raffle,
            StoreId = raffle.StoreId,
            Drawings = drawings,
            EligibleTicketsCount = eligible,
            PresenterToken = token,
            DrawActionUrl = Url.Action(nameof(DrawNext), "RafflePresent", new { raffleId, token }, Request.Scheme)!,
            DrawStateUrl = Url.Action(nameof(DrawState), "RafflePresent", new { raffleId, token }, Request.Scheme)!
        };

        return View("Present", vm);
    }

    [HttpGet("{raffleId}/present/draw-state")]
    public async Task<IActionResult> DrawState(Guid raffleId, [FromQuery] string token)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null) return NotFound();
        if (!TryValidateToken(token, raffle, out var bad)) return bad;

        var state = RaffleDrawStateBuilder.FromRaffle(raffle);
        return Json(new DrawStateResponse
        {
            Status = state.Status,
            TotalTickets = state.TotalTickets,
            EligibleTicketsRemaining = state.EligibleTicketsRemaining,
            DrawingsCount = state.DrawingsCount,
            CanDraw = state.CanDraw,
            CanUndoLastDraw = state.CanUndoLastDraw
        });
    }

    /// <summary>Draw next prize — requires valid presenter token (header or query).</summary>
    [HttpPost("{raffleId}/present/draw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DrawNext(Guid raffleId, [FromQuery] string token)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null) return NotFound();
        if (!TryValidateToken(token, raffle, out var bad)) return bad;

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

    private bool TryValidateToken(string? token, Raffle raffle, out IActionResult error)
    {
        if (_tokens.TryValidate(token, raffle.Id, raffle.StoreId, out _))
        {
            error = null!;
            return true;
        }

        error = Unauthorized(new { error = "Invalid or expired presenter token" });
        return false;
    }
}
