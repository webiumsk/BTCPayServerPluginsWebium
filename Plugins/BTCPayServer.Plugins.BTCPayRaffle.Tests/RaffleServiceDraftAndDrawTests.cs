using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

public class RaffleServiceDraftAndDrawTests
{
    private static RaffleService CreateService() =>
        new(new InMemoryRaffleDbFactory(), NullLogger<RaffleService>.Instance);

    [Fact]
    public async Task UpdateDraftRaffle_OnlyWhileDraft()
    {
        var svc = CreateService();
        var raffle = await svc.CreateRaffleAsync("store-1", "Test", null, "SATS", 1000);

        await svc.UpdateDraftRaffleAsync(raffle.Id, "Renamed", "desc", "SATS", 2000, 50);
        var updated = await svc.GetRaffleAsync(raffle.Id);
        Assert.Equal("Renamed", updated!.Name);
        Assert.Equal("desc", updated.Description);
        Assert.Equal(2000, updated.TicketPriceSats);
        Assert.Equal(50, updated.MaxTickets);

        await svc.OpenRaffleAsync(raffle.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateDraftRaffleAsync(raffle.Id, "X", null, "SATS", 1000, null));
    }

    [Fact]
    public async Task DrawNextPrize_AndGetDrawings()
    {
        var svc = CreateService();
        var raffle = await svc.CreateRaffleAsync("store-1", "Draw test", null, "SATS", 100);
        await svc.OpenRaffleAsync(raffle.Id);
        await svc.AddManualTicketsAsync(raffle.Id, 3, null, null);
        await svc.CloseRaffleSalesAsync(raffle.Id);

        var (drawing, winner) = await svc.DrawNextPrizeAsync(raffle.Id);
        Assert.Equal(1, drawing.DrawOrder);
        Assert.True(winner.TicketNumber >= 1);

        var drawings = await svc.GetDrawingsAsync(raffle.Id);
        Assert.Single(drawings);
        Assert.Equal(winner.TicketNumber, drawings[0].WinningTicket.TicketNumber);

        var afterDraw = await svc.GetRaffleAsync(raffle.Id);
        Assert.NotNull(afterDraw);
        var state = RaffleDrawStateBuilder.FromRaffle(afterDraw);
        Assert.Equal(2, state.EligibleTicketsRemaining);
        Assert.True(state.CanDraw);
    }
}
