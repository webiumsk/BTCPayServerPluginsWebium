using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Blitz;
using LNURL;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Blitz.Tests;

public class BlitzLnurlRequestFilterTests
{
    static JObject BlitzMeta(long min = 1000, long max = 10_000_000_000, int comment = 150) => JObject.Parse(
        $"{{\"tag\":\"payRequest\",\"minSendable\":{min},\"maxSendable\":{max},\"commentAllowed\":{comment}," +
        "\"metadata\":\"[[\\\"text/plain\\\",\\\"Pay alice\\\"],[\\\"text/identifier\\\",\\\"alice@blitzwalletapp.com\\\"]]\"}");

    [Fact]
    public void Mirrors_metadata_and_narrows_bounds()
    {
        var arg = new LNURLPayRequest
        {
            Metadata = "[[\"text/plain\",\"my store\"]]",
            MinSendable = LightMoney.MilliSatoshis(1),
            MaxSendable = LightMoney.MilliSatoshis(100_000_000_000),
            CommentAllowed = 2000
        };

        BlitzLnurlRequestFilter.ApplyBlitzParameters(arg, BlitzMeta());

        Assert.Contains("alice@blitzwalletapp.com", arg.Metadata);
        Assert.Equal(LightMoney.MilliSatoshis(1000), arg.MinSendable);           // raised to Blitz's min
        Assert.Equal(LightMoney.MilliSatoshis(10_000_000_000), arg.MaxSendable); // lowered to Blitz's max
        Assert.Equal(150, arg.CommentAllowed);                                   // capped to Blitz's limit
    }

    [Fact]
    public void Fixed_amount_invoice_within_bounds_is_preserved()
    {
        // BTCPay serves min == max for a fixed-amount checkout invoice; intersecting with Blitz's wide
        // range must keep it fixed.
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(21_000),
            MaxSendable = LightMoney.MilliSatoshis(21_000)
        };

        BlitzLnurlRequestFilter.ApplyBlitzParameters(arg, BlitzMeta());

        Assert.Equal(LightMoney.MilliSatoshis(21_000), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(21_000), arg.MaxSendable);
    }

    [Fact]
    public void Disjoint_ranges_leave_bounds_untouched()
    {
        // Blitz's min (1000 msat) above BTCPay's fixed 500 msat -> disjoint -> leave BTCPay's bounds.
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(500),
            MaxSendable = LightMoney.MilliSatoshis(500)
        };

        BlitzLnurlRequestFilter.ApplyBlitzParameters(arg, BlitzMeta(min: 1000));

        Assert.Equal(LightMoney.MilliSatoshis(500), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(500), arg.MaxSendable);
    }

    [Fact]
    public void Lower_commentAllowed_is_not_raised()
    {
        var arg = new LNURLPayRequest { CommentAllowed = 50 };
        BlitzLnurlRequestFilter.ApplyBlitzParameters(arg, BlitzMeta(comment: 150));
        Assert.Equal(50, arg.CommentAllowed); // only ever capped, never raised
    }

    [Fact]
    public void Detects_blitz_connection_strings_and_expands_bare_usernames()
    {
        Assert.True(BlitzLnurlRequestFilter.TryGetBlitzLnAddress("type=blitz;ln-address=alice", out var a));
        Assert.Equal("alice@blitzwalletapp.com", a);

        Assert.True(BlitzLnurlRequestFilter.TryGetBlitzLnAddress("type=blitz;ln-address=bob@other.example", out var b));
        Assert.Equal("bob@other.example", b);
    }

    [Fact]
    public void Ignores_non_blitz_and_malformed_connection_strings()
    {
        Assert.False(BlitzLnurlRequestFilter.TryGetBlitzLnAddress("type=lnd;server=https://x", out _));
        Assert.False(BlitzLnurlRequestFilter.TryGetBlitzLnAddress("type=blitz;", out _));
        Assert.False(BlitzLnurlRequestFilter.TryGetBlitzLnAddress(null, out _));
        Assert.False(BlitzLnurlRequestFilter.TryGetBlitzLnAddress("complete garbage", out _));
    }
}
