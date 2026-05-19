#nullable enable
using System;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class BuyerWalletPendingDraw
{
    public int DrawOrder { get; set; }
    public DateTimeOffset RevealAt { get; set; }
}
