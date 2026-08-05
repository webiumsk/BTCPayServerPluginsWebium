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

    [Theory]
    [InlineData(null, CashuMeltPaymentService.PriorMeltQuoteDecision.FreshMelt)]
    [InlineData("UNPAID", CashuMeltPaymentService.PriorMeltQuoteDecision.FreshMelt)]
    [InlineData("EXPIRED", CashuMeltPaymentService.PriorMeltQuoteDecision.FreshMelt)]
    [InlineData("PAID", CashuMeltPaymentService.PriorMeltQuoteDecision.CompleteSettlement)]
    [InlineData("paid", CashuMeltPaymentService.PriorMeltQuoteDecision.CompleteSettlement)]
    [InlineData("PENDING", CashuMeltPaymentService.PriorMeltQuoteDecision.WaitPending)]
    public void ClassifyPriorMeltQuote_MapsStateToDecision(
        string? state, CashuMeltPaymentService.PriorMeltQuoteDecision expected)
    {
        var quote = state is null
            ? null
            : new CashuMeltMintClient.MeltQuoteResponse("mq", 1000, 10, state, null, null, null);
        Assert.Equal(expected, CashuMeltPaymentService.ClassifyPriorMeltQuote(quote));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 0, 0)]      // free keyset
    [InlineData(0, 1000, 0)]   // no proofs
    [InlineData(5, 100, 1)]    // 500 ppk → ceil = 1 sat
    [InlineData(3, 1000, 3)]   // exactly 3 sat
    [InlineData(1, 999, 1)]    // 999 ppk → ceil = 1 sat
    [InlineData(10, 1001, 11)] // 10010 ppk → ceil = 11 sat
    public void KeysetInputFeeSat_CeilsPerThousand(int proofCount, long ppk, long expected)
    {
        Assert.Equal(expected, CashuMeltFeePolicy.KeysetInputFeeSat(proofCount, ppk));
    }

    [Fact]
    public void KeysetInputFeeSat_OverflowSafeForMintControlledPpk()
    {
        // input_fee_ppk comes from the mint - an absurd value must clamp, never wrap negative.
        var clamped = CashuMeltFeePolicy.KeysetInputFeeSat(1_000_000, long.MaxValue);
        Assert.Equal(long.MaxValue / 1000, clamped);

        var nearLimit = CashuMeltFeePolicy.KeysetInputFeeSat(2, long.MaxValue / 2);
        Assert.True(nearLimit > 0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]     // max(ceil(log2 1), 1) = 1
    [InlineData(2, 1)]     // ceil(log2 2) = 1
    [InlineData(3, 2)]
    [InlineData(180, 8)]   // real incident reserve: 2^7 < 180 <= 2^8
    [InlineData(1024, 10)]
    [InlineData(1025, 11)]
    public void BlankOutputCount_MatchesNut08Formula(long feeReserve, int expected)
    {
        Assert.Equal(expected, CashuMeltFeePolicy.BlankOutputCount(feeReserve));
    }

    [Theory]
    // Official NUT-00 hash_to_curve test vectors (message bytes → compressed point).
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000",
        "024cce997d3b518f739663b757deaec95bcd9473c30a14ac2fd04023a739d1a725")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000001",
        "022e7158e11c9506f1aa4248bf531298daa7febd6194f003edcd9b93ade6253acf")]
    public void ComputeYHex_MatchesNut00Vectors(string messageHex, string expectedY)
    {
        Assert.Equal(expectedY, CashuMeltCrypto.ComputeYHex(Convert.FromHexString(messageHex)));
    }

    [Fact]
    public void ComputeYHex_Utf8SecretConvention()
    {
        // Same convention the melt flow uses: Y over the UTF-8 bytes of the secret string.
        // Expected value independently computed with a reference implementation.
        Assert.Equal(
            "02992871637ec60a342e135ddac364f71932005d7ca6b7148981f0b7afc2005da4",
            CashuMeltCrypto.ComputeYHex(System.Text.Encoding.UTF8.GetBytes("abc123def456")));
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
