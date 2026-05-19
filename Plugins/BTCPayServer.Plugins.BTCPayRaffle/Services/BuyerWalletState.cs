#nullable enable
using System.Collections.Generic;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class BuyerWalletState
{
    public string Status { get; set; } = "";
    public List<int> TicketNumbers { get; set; } = new();
    public List<int> WinningNumbers { get; set; } = new();
    public List<int> MyWinningNumbers { get; set; } = new();
    public int DrawingsCount { get; set; }
    public int PurchaseCount { get; set; }
    public BuyerWalletPendingDraw? PendingDraw { get; set; }
}
