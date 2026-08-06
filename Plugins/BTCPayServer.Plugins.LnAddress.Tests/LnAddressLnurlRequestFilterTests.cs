using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LnAddress;
using LNURL;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LnAddress.Tests;

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
        // LnAddress's min (1000 msat) above BTCPay's fixed 500 msat -> disjoint -> leave BTCPay's bounds.
        var arg = new LNURLPayRequest
        {
            MinSendable = LightMoney.MilliSatoshis(500),
            MaxSendable = LightMoney.MilliSatoshis(500)
        };

        LnAddressLnurlRequestFilter.ApplyLnAddressParameters(arg, LnAddressMeta(min: 1000));

        Assert.Equal(LightMoney.MilliSatoshis(500), arg.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(500), arg.MaxSendable);
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
