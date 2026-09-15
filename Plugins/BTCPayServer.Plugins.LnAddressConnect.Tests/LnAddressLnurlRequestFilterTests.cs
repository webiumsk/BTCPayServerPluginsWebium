using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LnAddressConnect;
using LNURL;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LnAddressConnect.Tests;

public class LnAddressLnurlRequestFilterTests
{
    static JObject LnAddressMeta(long min = 1000, long max = 10_000_000_000, int comment = 140) => JObject.Parse(
        $"{{\"tag\":\"payRequest\",\"minSendable\":{min},\"maxSendable\":{max},\"commentAllowed\":{comment}," +
        "\"metadata\":\"[[\\\"text/plain\\\",\\\"Pay alice\\\"],[\\\"text/identifier\\\",\\\"alice@flashapp.me\\\"]]\"}");

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

        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta());

        Assert.Contains("alice@flashapp.me", arg.Metadata);
        Assert.Equal(LightMoney.MilliSatoshis(1000), arg.MinSendable);           // raised to LnAddress's min
        Assert.Equal(LightMoney.MilliSatoshis(10_000_000_000), arg.MaxSendable); // lowered to LnAddress's max
        Assert.Equal(140, arg.CommentAllowed);                                   // capped to LnAddress's limit
    }

    [Fact]
    public void Fixed_amount_invoice_within_bounds_is_preserved()
    {
        // BTCPay serves min == max for a fixed-amount checkout invoice; intersecting with LnAddress's wide
        // range must keep it fixed.
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(21_000),
            MaxSendable = LightMoney.MilliSatoshis(21_000)
        };

        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta());

        Assert.Equal(LightMoney.MilliSatoshis(21_000), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(21_000), arg.MaxSendable);
    }

    [Fact]
    public void Disjoint_ranges_leave_bounds_untouched()
    {
        // LnAddress's min (14 sat, as Blink advertises for some accounts) above BTCPay's fixed 5 sat
        // -> disjoint -> leave BTCPay's bounds.
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(5_000),
            MaxSendable = LightMoney.MilliSatoshis(5_000)
        };

        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta(min: 14_000));

        Assert.Equal(LightMoney.MilliSatoshis(5_000), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(5_000), arg.MaxSendable);
    }

    [Fact]
    public void Sub_satoshi_fixed_amount_is_rounded_up_to_whole_satoshi()
    {
        // BTCPay serves min == max == due amount in msat (usually sub-satoshi). Sat-only LNURL servers
        // (Blink) refuse sub-satoshi callbacks, so the served fixed amount rounds up to the next satoshi.
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(5_137_620),
            MaxSendable = LightMoney.MilliSatoshis(5_137_620)
        };

        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta());

        Assert.Equal(LightMoney.MilliSatoshis(5_138_000), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(5_138_000), arg.MaxSendable);
    }

    [Fact]
    public void Rounding_never_exceeds_the_wallets_maximum()
    {
        // The wallet's own max is sub-satoshi and less than 1 sat above BTCPay's fixed amount: no
        // whole-satoshi amount fits, so the bounds stay as served rather than being widened past a
        // maximum the LNURL server would reject.
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(5_137_620),
            MaxSendable = LightMoney.MilliSatoshis(5_137_620)
        };

        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta(max: 5_137_800));

        Assert.Equal(LightMoney.MilliSatoshis(5_137_620), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(5_137_620), arg.MaxSendable);
    }

    [Fact]
    public void Range_bounds_round_inward_to_whole_satoshis()
    {
        // Top-up style range: min rounds up, max rounds down, whole-satoshi bounds stay put.
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(1_500),
            MaxSendable = LightMoney.MilliSatoshis(5_137_620)
        };

        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta());

        Assert.Equal(LightMoney.MilliSatoshis(2_000), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(5_137_000), arg.MaxSendable);

        var whole = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(21_000),
            MaxSendable = LightMoney.MilliSatoshis(21_000)
        };
        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(whole, LnAddressMeta());
        Assert.Equal(LightMoney.MilliSatoshis(21_000), whole.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(21_000), whole.MaxSendable);
    }

    [Fact]
    public void Lower_commentAllowed_is_not_raised()
    {
        var arg = new LNURLPayRequest { CommentAllowed = 50 };
        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta(comment: 140));
        Assert.Equal(50, arg.CommentAllowed); // only ever capped, never raised
    }

    [Fact]
    public void Negative_send_limits_are_treated_as_absent()
    {
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(1000),
            MaxSendable = LightMoney.MilliSatoshis(50_000)
        };

        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta(min: -1, max: -5));

        Assert.Equal(LightMoney.MilliSatoshis(1000), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(50_000), arg.MaxSendable);
    }

    [Fact]
    public void Negative_commentAllowed_is_ignored()
    {
        var arg = new LNURLPayRequest { CommentAllowed = 2000 };
        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta(comment: -1));
        Assert.Equal(2000, arg.CommentAllowed);
    }

    [Fact]
    public void Detects_flash_connection_strings_and_expands_bare_usernames()
    {
        Assert.True(LnAddressLnurlRequestFilter.TryGetLnAddressLnAddress("type=flash;ln-address=alice", out var a));
        Assert.Equal("alice@flashapp.me", a);

        Assert.True(LnAddressLnurlRequestFilter.TryGetLnAddressLnAddress("type=flash;ln-address=bob@other.example", out var b));
        Assert.Equal("bob@other.example", b);
    }

    [Fact]
    public void Ignores_non_flash_and_malformed_connection_strings()
    {
        Assert.False(LnAddressLnurlRequestFilter.TryGetLnAddressLnAddress("type=lnd;server=https://x", out _));
        Assert.False(LnAddressLnurlRequestFilter.TryGetLnAddressLnAddress("type=flash;", out _));
        Assert.False(LnAddressLnurlRequestFilter.TryGetLnAddressLnAddress(null, out _));
        Assert.False(LnAddressLnurlRequestFilter.TryGetLnAddressLnAddress("complete garbage", out _));
    }

    [Fact]
    public async Task Metadata_fetch_is_cached_within_ttl()
    {
        // The hook runs on every LNURL-pay request during checkout; repeated calls within the TTL
        // must not produce one outbound fetch each.
        var user = "cachef" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        var uri = new System.Uri($"https://flashapp.me/.well-known/lnurlp/{user}");
        var fake = new FakeHttp().Map(uri.ToString(), LnAddressMeta().ToString());

        var j1 = await LnAddressLnurlRequestFilter.FetchMetadataCached(fake.Client(), uri, TestContext.Current.CancellationToken);
        var j2 = await LnAddressLnurlRequestFilter.FetchMetadataCached(fake.Client(), uri, TestContext.Current.CancellationToken);

        Assert.NotNull(j1);
        Assert.NotNull(j2);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Failed_metadata_fetch_is_not_cached()
    {
        var user = "cachee" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        var uri = new System.Uri($"https://flashapp.me/.well-known/lnurlp/{user}");
        var fake = new FakeHttp(); // unmapped -> 404

        var j1 = await LnAddressLnurlRequestFilter.FetchMetadataCached(fake.Client(), uri, TestContext.Current.CancellationToken);
        var j2 = await LnAddressLnurlRequestFilter.FetchMetadataCached(fake.Client(), uri, TestContext.Current.CancellationToken);

        Assert.Null(j1);
        Assert.Null(j2);
        Assert.Equal(2, fake.Requests.Count); // errors are retried, not cached
    }
}
