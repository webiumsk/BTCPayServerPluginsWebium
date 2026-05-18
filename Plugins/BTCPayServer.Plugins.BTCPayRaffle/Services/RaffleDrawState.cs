#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class RaffleDrawState
{
    public string Status { get; init; } = "";
    public int TotalTickets { get; init; }
    public int EligibleTicketsRemaining { get; init; }
    public int DrawingsCount { get; init; }
    public bool CanDraw { get; init; }
    public bool CanUndoLastDraw { get; init; }
}

public static class RaffleDrawStateBuilder
{
    public static RaffleDrawState FromRaffle(Raffle raffle)
    {
        var drawnIds = raffle.Drawings.Select(d => d.WinningTicketId).ToHashSet();
        var eligible = raffle.Tickets.Count(t => !drawnIds.Contains(t.Id));
        var canDraw = raffle.Status is RaffleStatus.Closed or RaffleStatus.Drawing && eligible > 0;

        return new RaffleDrawState
        {
            Status = raffle.Status.ToString(),
            TotalTickets = raffle.Tickets.Count,
            EligibleTicketsRemaining = eligible,
            DrawingsCount = raffle.Drawings.Count,
            CanDraw = canDraw,
            CanUndoLastDraw = raffle.Status == RaffleStatus.Drawing && raffle.Drawings.Count > 0
        };
    }

    public static async Task<RaffleDrawState> GetDrawStateAsync(this RaffleService service, Guid raffleId)
    {
        var raffle = await service.GetRaffleAsync(raffleId)
            ?? throw new System.InvalidOperationException("Raffle not found");
        return FromRaffle(raffle);
    }
}
