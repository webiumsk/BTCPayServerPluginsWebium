using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using Xunit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

public class RafflePricingTests
{
    [Fact]
    public void ApplyPricing_Sats_SyncsTicketPriceSats()
    {
        var raffle = new Raffle();
        RafflePricing.ApplyPricing(raffle, "SATS", 21_000);
        Assert.Equal("SATS", raffle.TicketCurrency);
        Assert.Equal(21_000, raffle.TicketPriceSats);
        Assert.Equal(21_000m, raffle.TicketPrice);
    }

    [Fact]
    public void ApplyPricing_Fiat_ClearsTicketPriceSats()
    {
        var raffle = new Raffle { TicketPriceSats = 100 };
        RafflePricing.ApplyPricing(raffle, "EUR", 5.5m);
        Assert.Equal("EUR", raffle.TicketCurrency);
        Assert.Equal(5.5m, raffle.TicketPrice);
        Assert.Equal(0, raffle.TicketPriceSats);
    }

    [Fact]
    public void ApplyPricing_Sats_RejectsFractional()
    {
        var raffle = new Raffle();
        Assert.Throws<ArgumentException>(() => RafflePricing.ApplyPricing(raffle, "SATS", 1.5m));
    }

    [Fact]
    public void DisplayTicketPriceSats_OnlyForSatsCurrency()
    {
        var sats = new Raffle();
        RafflePricing.ApplyPricing(sats, "SATS", 100);
        Assert.Equal(100L, RafflePricing.DisplayTicketPriceSats(sats));

        var eur = new Raffle();
        RafflePricing.ApplyPricing(eur, "EUR", 10m);
        Assert.Null(RafflePricing.DisplayTicketPriceSats(eur));
    }

    [Fact]
    public void RaffleTicketIds_ManualPrefix()
    {
        var id = RaffleTicketIds.NewManual();
        Assert.StartsWith(RaffleTicketIds.ManualPrefix, id);
        Assert.True(RaffleTicketIds.IsManual(id));
        Assert.False(RaffleTicketIds.IsManual("inv_abc"));
    }
}
