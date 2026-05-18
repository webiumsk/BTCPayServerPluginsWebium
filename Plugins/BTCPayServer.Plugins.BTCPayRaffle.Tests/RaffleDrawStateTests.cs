using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using Xunit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

public class RaffleDrawStateTests
{
    [Fact]
    public void FromRaffle_ClosedWithTickets_CanDraw()
    {
        var ticket = new RaffleTicket { Id = Guid.NewGuid(), TicketNumber = 1, InvoiceId = "inv_1" };
        var raffle = new Raffle
        {
            Status = RaffleStatus.Closed,
            Tickets = new List<RaffleTicket> { ticket },
            Drawings = new List<RaffleDrawing>()
        };

        var state = RaffleDrawStateBuilder.FromRaffle(raffle);

        Assert.Equal("Closed", state.Status);
        Assert.Equal(1, state.TotalTickets);
        Assert.Equal(1, state.EligibleTicketsRemaining);
        Assert.True(state.CanDraw);
        Assert.False(state.CanUndoLastDraw);
    }

    [Fact]
    public void FromRaffle_DrawingWithDrawings_CanUndo()
    {
        var ticket = new RaffleTicket { Id = Guid.NewGuid(), TicketNumber = 5, InvoiceId = "inv_1" };
        var drawing = new RaffleDrawing
        {
            DrawOrder = 1,
            WinningTicketId = ticket.Id,
            WinningTicket = ticket
        };
        var raffle = new Raffle
        {
            Status = RaffleStatus.Drawing,
            Tickets = new List<RaffleTicket> { ticket },
            Drawings = new List<RaffleDrawing> { drawing }
        };

        var state = RaffleDrawStateBuilder.FromRaffle(raffle);

        Assert.Equal(0, state.EligibleTicketsRemaining);
        Assert.False(state.CanDraw);
        Assert.True(state.CanUndoLastDraw);
    }
}
