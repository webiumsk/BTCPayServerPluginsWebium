using BTCPayServer.Plugins.CashuMelt.Services;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

public class CashuMeltAmountCalculatorTests
{
    [Fact]
    public void SatsInvoice_UsesInvoicePrice_NotBtcMultiplier()
    {
        var amount = CashuMeltAmountCalculator.ComputeMintAmount("SATS", 5000m, 0.00005m, "sat");
        Assert.Equal(5000, amount);
    }

    [Fact]
    public void SatsInvoice_MinimumOneSat()
    {
        var amount = CashuMeltAmountCalculator.ComputeMintAmount("SATS", 0m, 0m, "sat");
        Assert.Equal(1, amount);
    }

    [Fact]
    public void BtcInvoice_ConvertsDueToSats()
    {
        var amount = CashuMeltAmountCalculator.ComputeMintAmount("BTC", 0.00001m, 0.00001m, "sat");
        Assert.Equal(1000, amount);
    }

    [Fact]
    public void UsdUnit_UsesCentsFromDue()
    {
        var amount = CashuMeltAmountCalculator.ComputeMintAmount("USD", 10.50m, 10.50m, "usd");
        Assert.Equal(1050, amount);
    }

    [Fact]
    public void SatsInvoice_WithUsdMintUnit_UsesPromptDueInUsdCents()
    {
        var amount = CashuMeltAmountCalculator.ComputeMintAmount("SATS", 5000m, 1.25m, "usd");
        Assert.Equal(125, amount);
    }
}
