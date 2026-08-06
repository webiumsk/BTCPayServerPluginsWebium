using System;
using BTCPayServer.Plugins.LnAddressConnect;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LnAddressConnect.Tests;

public class ConnectionStringTests
{
    const string PayTemplate =
        "{\"tag\":\"payRequest\",\"status\":\"OK\",\"callback\":\"https://ibex.flashapp.me/pay/lnurl/{U}\",\"minSendable\":1000,\"maxSendable\":10000000000,\"commentAllowed\":140,\"metadata\":\"[[\\\"text/plain\\\",\\\"Pay {U}\\\"]]\"}";

    [Fact]
    public void Ignores_foreign_types()
    {
        var h = new LnAddressConnectionStringHandler(new FakeHttpClientFactory(new FakeHttp()), NullLoggerFactory.Instance);
        var client = h.Create("type=lnd;server=https://x", Network.Main, out var error);
        Assert.Null(client);
        Assert.Null(error);
    }

    [Fact]
    public void Accepts_primary_type_with_full_address()
    {
        var user = "primary" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var fake = new FakeHttp().Map(
            $"https://anywallet.example/.well-known/lnurlp/{user}", PayTemplate.Replace("{U}", user));
        var h = new LnAddressConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);
        var client = h.Create($"type=lnaddress;ln-address={user}@anywallet.example", Network.Main, out var error);
        Assert.Null(error);
        Assert.NotNull(client);
    }

    [Fact]
    public void Primary_type_rejects_bare_username()
    {
        var h = new LnAddressConnectionStringHandler(new FakeHttpClientFactory(new FakeHttp()), NullLoggerFactory.Instance);
        var client = h.Create("type=lnaddress;ln-address=alice", Network.Main, out var error);
        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("user@domain", error);
    }

    [Fact]
    public void Legacy_aliases_expand_bare_usernames_to_their_domains()
    {
        var user = "legacy" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var fake = new FakeHttp()
            .Map($"https://blitzwalletapp.com/.well-known/lnurlp/{user}", PayTemplate.Replace("{U}", user))
            .Map($"https://flashapp.me/.well-known/lnurlp/{user}", PayTemplate.Replace("{U}", user));
        var h = new LnAddressConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        var blitz = h.Create($"type=blitz;ln-address={user}", Network.Main, out var e1);
        Assert.Null(e1);
        Assert.NotNull(blitz);

        var flash = h.Create($"type=flash;ln-address={user}", Network.Main, out var e2);
        Assert.Null(e2);
        Assert.NotNull(flash);
        // Same bare username, two different legacy defaults - both domains were queried.
        Assert.Contains(fake.Requests, r => r.Contains("blitzwalletapp.com"));
        Assert.Contains(fake.Requests, r => r.Contains("flashapp.me"));
    }

    [Fact]
    public void Rejects_missing_ln_address()
    {
        var h = new LnAddressConnectionStringHandler(new FakeHttpClientFactory(new FakeHttp()), NullLoggerFactory.Instance);
        var client = h.Create("type=flash;", Network.Main, out var error);
        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("ln-address", error);
    }

    [Fact]
    public void Unknown_address_yields_error_not_throw()
    {
        // FakeHttp answers 404 for any unmapped route -> resolution fails -> error, no exception,
        // and failures are not cached (a later Create retries).
        var h = new LnAddressConnectionStringHandler(new FakeHttpClientFactory(new FakeHttp()), NullLoggerFactory.Instance);
        var client = h.Create($"type=flash;ln-address=missing{Guid.NewGuid():N}", Network.Main, out var error);
        Assert.Null(client);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Bare_username_resolves_on_default_domain_and_is_cached()
    {
        // Unique user so the process-wide static resolution cache isn't pre-populated by another test.
        var user = "cache" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var fake = new FakeHttp().Map(
            $"https://flashapp.me/.well-known/lnurlp/{user}", PayTemplate.Replace("{U}", user));
        var h = new LnAddressConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        var c1 = h.Create($"type=flash;ln-address={user}", Network.Main, out var e1);
        var c2 = h.Create($"type=flash;ln-address={user}", Network.Main, out var e2);

        Assert.Null(e1);
        Assert.Null(e2);
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        // First Create resolved over the network (against the DEFAULT domain); the second hit the cache.
        Assert.Single(fake.Requests);
        Assert.Contains("flashapp.me", fake.Requests[0]);
    }

    [Fact]
    public async Task Concurrent_cache_misses_share_one_resolution()
    {
        var user = "flight" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var fake = new FakeHttp().Map(
            $"https://flashapp.me/.well-known/lnurlp/{user}", PayTemplate.Replace("{U}", user));
        // Park the resolution response so the 8 callers demonstrably overlap instead of
        // resolving synchronously one after another.
        fake.HoldResponses();
        var h = new LnAddressConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var client = h.Create($"type=flash;ln-address={user}", Network.Main, out var error);
            return (client, error);
        })).ToArray();

        // Exactly one resolution reaches the network while it is held; the other callers
        // queue behind the shared in-flight task and none completes early.
        for (int i = 0; i < 500 && fake.WaitingCount == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, fake.WaitingCount);
        Assert.All(tasks, t => Assert.False(t.IsCompleted));

        fake.ReleaseResponses();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => { Assert.NotNull(r.client); Assert.Null(r.error); });
        // Single-flight: the 8 concurrent misses collapsed into one network resolution.
        Assert.Single(fake.Requests);
    }
}
