using System;
using BTCPayServer.Plugins.Blitz;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Blitz.Tests;

public class ConnectionStringTests
{
    const string PayTemplate =
        "{\"tag\":\"payRequest\",\"status\":\"OK\",\"callback\":\"https://blitzwalletapp.com/.well-known/lnurlp/{U}/\",\"minSendable\":1000,\"maxSendable\":10000000000,\"commentAllowed\":150,\"metadata\":\"[[\\\"text/plain\\\",\\\"Pay {U}\\\"]]\"}";

    [Fact]
    public void Ignores_non_blitz_types()
    {
        var h = new BlitzConnectionStringHandler(new FakeHttpClientFactory(new FakeHttp()), NullLoggerFactory.Instance);
        var client = h.Create("type=lnd;server=https://x", Network.Main, out var error);
        Assert.Null(client);
        Assert.Null(error);
    }

    [Fact]
    public void Rejects_missing_ln_address()
    {
        var h = new BlitzConnectionStringHandler(new FakeHttpClientFactory(new FakeHttp()), NullLoggerFactory.Instance);
        var client = h.Create("type=blitz;", Network.Main, out var error);
        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("ln-address", error);
    }

    [Fact]
    public void Unknown_address_yields_error_not_throw()
    {
        // FakeHttp answers 404 for any unmapped route -> resolution fails -> error, no exception,
        // and failures are not cached (a later Create retries).
        var h = new BlitzConnectionStringHandler(new FakeHttpClientFactory(new FakeHttp()), NullLoggerFactory.Instance);
        var client = h.Create($"type=blitz;ln-address=missing{Guid.NewGuid():N}", Network.Main, out var error);
        Assert.Null(client);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Bare_username_resolves_on_default_domain_and_is_cached()
    {
        // Unique user so the process-wide static resolution cache isn't pre-populated by another test.
        var user = "cache" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var fake = new FakeHttp().Map(
            $"https://blitzwalletapp.com/.well-known/lnurlp/{user}", PayTemplate.Replace("{U}", user));
        var h = new BlitzConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        var c1 = h.Create($"type=blitz;ln-address={user}", Network.Main, out var e1);
        var c2 = h.Create($"type=blitz;ln-address={user}", Network.Main, out var e2);

        Assert.Null(e1);
        Assert.Null(e2);
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        // First Create resolved over the network (against the DEFAULT domain); the second hit the cache.
        Assert.Single(fake.Requests);
        Assert.Contains("blitzwalletapp.com", fake.Requests[0]);
    }

    [Fact]
    public async Task Concurrent_cache_misses_share_one_resolution()
    {
        var user = "flight" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var fake = new FakeHttp().Map(
            $"https://blitzwalletapp.com/.well-known/lnurlp/{user}", PayTemplate.Replace("{U}", user));
        var h = new BlitzConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var client = h.Create($"type=blitz;ln-address={user}", Network.Main, out var error);
            return (client, error);
        })));

        Assert.All(results, r => { Assert.NotNull(r.client); Assert.Null(r.error); });
        // Single-flight: the 8 concurrent misses collapsed into one network resolution.
        Assert.Single(fake.Requests);
    }
}
