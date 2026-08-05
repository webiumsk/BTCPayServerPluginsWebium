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

    [Theory]
    [InlineData(1, 2)]          // floor of 2 sat
    [InlineData(100, 2)]
    [InlineData(10_000, 100)]
    [InlineData(18_058, 181)]   // regression: old 100-sat cap made this undershoot a 180-sat reserve
    [InlineData(1_000_000, 10_000)]
    public void EstimateFeeBufferSat_ScalesWithAmount(long amount, long expected)
    {
        Assert.Equal(expected, CashuMeltFeePolicy.EstimateFeeBufferSat(amount));
    }

    [Fact]
    public void EstimateFeeBufferSat_CoversOnePercentReserveOfForwardAmount()
    {
        // For any minted amount, forward = minted - buffer must satisfy
        // forward + ceil(1% of forward) <= minted (the common mint reserve policy).
        foreach (var minted in new long[] { 500, 5_000, 18_058, 50_000, 123_457 })
        {
            var forward = minted - CashuMeltFeePolicy.EstimateFeeBufferSat(minted);
            var reserve = Math.Max(2, (long)Math.Ceiling(forward * 0.01));
            Assert.True(forward + reserve <= minted,
                $"minted={minted} forward={forward} reserve={reserve}");
        }
    }

    [Fact]
    public void ReducedForwardSat_RegressionFor18058SatPayment()
    {
        // Real incident: 18058 sat minted, old buffer capped at 100 → forward 17958,
        // mint quoted a 180 sat reserve → 18138 needed > 18058 minted → hard fail.
        // The adjustment must retry with 18058 - 180 = 17878.
        var reduced = CashuMeltFeePolicy.ReducedForwardSat(18_058, 180, 17_958);
        Assert.Equal(17_878, reduced);
    }

    [Fact]
    public void ReducedForwardSat_NullWhenNotConverging()
    {
        // Reserve eats the whole minted amount.
        Assert.Null(CashuMeltFeePolicy.ReducedForwardSat(100, 100, 50));
        // Reduced amount would not shrink (LNURL minimum clamped it back up).
        Assert.Null(CashuMeltFeePolicy.ReducedForwardSat(1_000, 10, 990));
    }
}
