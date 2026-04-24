using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Services;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

public class CashuMeltPolicyTests
{
    [Fact]
    public void NormalizeMintUrl_TrimsTrailingSlash()
    {
        Assert.Equal("https://mint.example/path", CashuMeltMintPolicy.NormalizeMintUrl("https://mint.example/path/"));
    }

    [Fact]
    public void ParseTrustedMintLines_SplitsCommaAndNewline()
    {
        var list = CashuMeltMintPolicy.ParseTrustedMintLines("https://a/x,\nhttps://b/y\r\n");
        Assert.Equal(2, list.Count);
        Assert.Contains("https://a/x", list);
        Assert.Contains("https://b/y", list);
    }

    [Fact]
    public void ValidateStoreMintAgainstTrustedList_AllowsEmptyTrustedList()
    {
        var s = new CashuMeltStoreSettings
        {
            StoreId = "s",
            MintUrl = "https://mint.example/x",
            TrustedMintUrls = null
        };
        CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(s);
    }

    [Fact]
    public void ValidateStoreMintAgainstTrustedList_RejectsMintNotInList()
    {
        var s = new CashuMeltStoreSettings
        {
            StoreId = "s",
            MintUrl = "https://mint.example/x",
            TrustedMintUrls = "https://other.example/y"
        };
        Assert.Throws<InvalidOperationException>(() => CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(s));
    }

    [Fact]
    public void FeePolicy_RespectsSatsCap()
    {
        var err = CashuMeltFeePolicy.ValidateMeltFeeReserve(10_000, 500, 400, null);
        Assert.NotNull(err);
    }

    [Fact]
    public void FeePolicy_RespectsPercentCap()
    {
        var err = CashuMeltFeePolicy.ValidateMeltFeeReserve(1000, 200, null, 10m);
        Assert.NotNull(err);
    }

    [Fact]
    public void FeePolicy_AllowsWithinCaps()
    {
        Assert.Null(CashuMeltFeePolicy.ValidateMeltFeeReserve(10_000, 100, 500, 5m));
    }

    [Fact]
    public void SettingsValidation_RejectsBadPercent()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CashuMeltSettingsValidation.ValidateOptionalFeeCaps(null, 101m));
    }
}
