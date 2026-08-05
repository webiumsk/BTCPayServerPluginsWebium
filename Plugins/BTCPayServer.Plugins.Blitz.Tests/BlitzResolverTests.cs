using System.Threading.Tasks;
using BTCPayServer.Plugins.Blitz;
using Xunit;

namespace BTCPayServer.Plugins.Blitz.Tests;

public class BlitzResolverTests
{
    // Blitz-shaped LUD-06 metadata: callback is the lnurlp URL with a trailing slash.
    const string Pay =
        "{\"tag\":\"payRequest\",\"status\":\"OK\",\"callback\":\"https://blitzwalletapp.com/.well-known/lnurlp/alice/\",\"minSendable\":1000,\"maxSendable\":10000000000,\"commentAllowed\":150,\"metadata\":\"[[\\\"text/plain\\\",\\\"Pay alice\\\"]]\"}";

    [Fact]
    public void Bare_username_expands_to_default_domain()
    {
        Assert.Equal("alice@blitzwalletapp.com", BlitzResolver.NormalizeAddress("alice"));
        Assert.Equal("alice@other.example", BlitzResolver.NormalizeAddress("alice@other.example"));
        Assert.Equal(("alice", "blitzwalletapp.com"), BlitzResolver.ParseLightningAddress(" alice "));
    }

    [Fact]
    public async Task Full_address_resolves_to_lnurlp_endpoint()
    {
        var http = new FakeHttp().Map("https://blitzwalletapp.com/.well-known/lnurlp/alice", Pay);
        var r = await BlitzResolver.Resolve("alice@blitzwalletapp.com", http.Client(), TestContext.Current.CancellationToken);
        Assert.Equal("https://blitzwalletapp.com/.well-known/lnurlp/alice", r.PayEndpoint.ToString());
        Assert.Equal("blitzwalletapp.com", r.DisplayHost);
        Assert.Equal("alice@blitzwalletapp.com", r.LnAddress);
    }

    [Fact]
    public async Task Non_payRequest_tag_is_rejected()
    {
        var http = new FakeHttp().Map("https://blitzwalletapp.com/.well-known/lnurlp/bob",
            "{\"tag\":\"withdrawRequest\",\"callback\":\"https://x/w\",\"k1\":\"abc\"}");
        await Assert.ThrowsAsync<System.FormatException>(() =>
            BlitzResolver.Resolve("bob", http.Client(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Error_status_surfaces_reason()
    {
        // Blitz returns this exact shape for unknown users.
        var http = new FakeHttp().Map("https://blitzwalletapp.com/.well-known/lnurlp/ghost",
            "{\"status\":\"ERROR\",\"reason\":\"No account found\"}");
        var ex = await Assert.ThrowsAsync<System.FormatException>(() =>
            BlitzResolver.Resolve("ghost", http.Client(), TestContext.Current.CancellationToken));
        Assert.Contains("No account found", ex.Message);
    }
}
