using BTCPayServer.Plugins.LnAddressConnect;
using Xunit;

namespace BTCPayServer.Plugins.LnAddressConnect.Tests;

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

    [Fact]
    public void Legacy_types_are_not_claimed_while_the_superseded_plugin_is_installed()
    {
        // Coexistence guard: when the Blitz/Flash plugin assembly is loaded, this
        // plugin must leave the legacy type to it (deterministic dispatch, one poller).
        static bool AllLegacyLoaded(string _) => true;
        static bool NoneLoaded(string _) => false;

        Assert.False(LnAddressTypes.ClaimsLegacyType("blitz", AllLegacyLoaded));
        Assert.False(LnAddressTypes.ClaimsLegacyType("flash", AllLegacyLoaded));

        Assert.True(LnAddressTypes.ClaimsLegacyType("blitz", NoneLoaded));
        Assert.True(LnAddressTypes.ClaimsLegacyType("flash", NoneLoaded));

        // The primary type is never subject to the guard.
        Assert.False(LnAddressTypes.ClaimsLegacyType("lnaddress", NoneLoaded));
        Assert.True(LnAddressTypes.IsOurType("lnaddress"));
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
