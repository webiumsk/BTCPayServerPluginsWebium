using System.Threading.Tasks;
using BTCPayServer.Plugins.LnAddressConnect;
using Xunit;

namespace BTCPayServer.Plugins.LnAddressConnect.Tests;

public class LnAddressResolverTests
{
    // LnAddress-shaped LUD-06 metadata: callback is the lnurlp URL with a trailing slash.
    const string Pay =
        "{\"tag\":\"payRequest\",\"status\":\"OK\",\"callback\":\"https://ibex.flashapp.me/pay/lnurl/alice\",\"minSendable\":1000,\"maxSendable\":10000000000,\"commentAllowed\":140,\"metadata\":\"[[\\\"text/plain\\\",\\\"Pay alice\\\"]]\"}";

    [Fact]
    public void Bare_username_expands_only_for_legacy_types()
    {
        // Legacy blitz/flash types keep their historical default-domain expansion.
        Assert.Equal("alice@flashapp.me", LnAddressResolver.NormalizeAddress("alice", "flash"));
        Assert.Equal("alice@blitzwalletapp.com", LnAddressResolver.NormalizeAddress("alice", "blitz"));
        // Full addresses always pass through, regardless of type.
        Assert.Equal("alice@other.example", LnAddressResolver.NormalizeAddress("alice@other.example", "lnaddress"));
        Assert.Equal("alice@other.example", LnAddressResolver.NormalizeAddress("alice@other.example"));
        // The primary type requires a full address - no silent domain guessing.
        Assert.Throws<System.FormatException>(() => LnAddressResolver.NormalizeAddress("alice", "lnaddress"));
        Assert.Throws<System.FormatException>(() => LnAddressResolver.NormalizeAddress("alice"));
        Assert.Equal(("alice", "flashapp.me"), LnAddressResolver.ParseLightningAddress(" alice@flashapp.me "));
        Assert.Throws<System.FormatException>(() => LnAddressResolver.ParseLightningAddress("alice"));
    }

    [Fact]
    public async Task Full_address_resolves_to_lnurlp_endpoint()
    {
        var http = new FakeHttp().Map("https://flashapp.me/.well-known/lnurlp/alice", Pay);
        var r = await LnAddressResolver.Resolve("alice@flashapp.me", http.Client(), TestContext.Current.CancellationToken);
        Assert.Equal("https://flashapp.me/.well-known/lnurlp/alice", r.PayEndpoint.ToString());
        Assert.Equal("flashapp.me", r.DisplayHost);
        Assert.Equal("alice@flashapp.me", r.LnAddress);
    }

    [Fact]
    public async Task Non_payRequest_tag_is_rejected()
    {
        var http = new FakeHttp().Map("https://flashapp.me/.well-known/lnurlp/bob",
            "{\"tag\":\"withdrawRequest\",\"callback\":\"https://x/w\",\"k1\":\"abc\"}");
        await Assert.ThrowsAsync<System.FormatException>(() =>
            LnAddressResolver.Resolve("bob@flashapp.me", http.Client(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Error_status_surfaces_reason()
    {
        // LnAddress returns this exact shape for unknown users.
        var http = new FakeHttp().Map("https://flashapp.me/.well-known/lnurlp/ghost",
            "{\"status\":\"ERROR\",\"reason\":\"No account found\"}");
        var ex = await Assert.ThrowsAsync<System.FormatException>(() =>
            LnAddressResolver.Resolve("ghost@flashapp.me", http.Client(), TestContext.Current.CancellationToken));
        Assert.Contains("No account found", ex.Message);
    }
}
