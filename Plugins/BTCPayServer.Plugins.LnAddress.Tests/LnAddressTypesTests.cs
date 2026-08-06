using BTCPayServer.Plugins.LnAddress;
using Xunit;

namespace BTCPayServer.Plugins.LnAddress.Tests;

public class LnAddressTypesTests
{
    [Theory]
    [InlineData("lnaddress", true)]
    [InlineData("LNADDRESS", true)]
    [InlineData("blitz", true)]
    [InlineData("flash", true)]
    [InlineData("blink", false)]
    [InlineData("lnurl", false)]
    [InlineData(null, false)]
    public void IsOurType_covers_primary_and_legacy_aliases(string? type, bool expected)
    {
        Assert.Equal(expected, LnAddressTypes.IsOurType(type));
    }

    [Theory]
    [InlineData("blitzwalletapp.com", "Blitz Wallet")]
    [InlineData("flashapp.me", "Flash")]
    [InlineData("coinos.io", "Coinos")]
    [InlineData("CoinOS.io", "Coinos")]
    [InlineData("unknown.example", "LN Address (unknown.example)")]
    public void DisplayNameFor_uses_curated_brands_with_generic_fallback(string domain, string expected)
    {
        Assert.Equal(expected, LnAddressTypes.DisplayNameFor(domain));
    }
}
