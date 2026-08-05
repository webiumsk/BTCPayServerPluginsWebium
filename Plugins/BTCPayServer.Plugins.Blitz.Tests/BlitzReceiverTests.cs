using System;
using System.Net;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Blitz;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Blitz.Tests;

public class BlitzReceiverTests
{
    // Blitz-shaped LUD-06 metadata: the callback is the lnurlp URL itself with a trailing slash.
    const string PayMeta =
        "{\"tag\":\"payRequest\",\"status\":\"OK\",\"callback\":\"{CB}\",\"minSendable\":1000,\"maxSendable\":10000000000,\"commentAllowed\":150,\"metadata\":\"[[\\\"text/plain\\\",\\\"Pay alice\\\"]]\"}";

    // Blitz-shaped LUD-21 verify URL.
    const string SparkVerify =
        "https://blitzwalletapp.com/.well-known/lnurlverify/SparkLightningReceiveRequest:019fd2f3-2432-cd96-0000-672abc";

    // Canonical BOLT#11 spec example (mainnet, 250,000,000 msat) — parses offline.
    const string SpecBolt11 =
        "lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";

    static ResolvedBlitz Resolved(string host) =>
        new(new Uri($"https://{host}/.well-known/lnurlp/alice"), $"alice@{host}", host);

    static BlitzReceiver Rx(string host, FakeHttp http, Network? network = null) =>
        new(Resolved(host), network ?? Network.Main, http.Client(), NullLogger.Instance);

    [Fact]
    public void Preimage_validation_matches_sha256()
    {
        var preimage = new string('0', 64); // 32 zero bytes
        var hash = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"; // sha256(32*0x00)
        Assert.True(BlitzReceiver.IsValidPreimage(preimage, hash));
        Assert.False(BlitzReceiver.IsValidPreimage(preimage, new string('1', 64)));
        Assert.False(BlitzReceiver.IsValidPreimage("xyz", hash));
        Assert.False(BlitzReceiver.IsValidPreimage(null, hash));
    }

    [Fact]
    public async Task Amountless_invoice_is_not_supported()
    {
        var rx = Rx("rx0.example", new FakeHttp());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            rx.CreateInvoice(null, "x", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInvoice_transient_transport_error_returns_unpaid_not_null()
    {
        var host = "rx.example";
        var hash = new string('a', 64);
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", $"https://{host}/verify/{hash}", host, $"https://{host}/.well-known/lnurlp/alice",
            DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp().Map($"https://{host}/verify/{hash}", "{}", HttpStatusCode.InternalServerError);
        var rx = Rx(host, http);

        var inv = await rx.GetInvoice(hash, TestContext.Current.CancellationToken);

        Assert.NotNull(inv);
        Assert.Equal(LightningInvoiceStatus.Unpaid, inv!.Status);
        TrackedInvoiceRegistry.Remove(hash);
    }

    [Fact]
    public async Task GetInvoice_error_status_returns_null()
    {
        var host = "rx2.example";
        var hash = new string('b', 64);
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", $"https://{host}/verify/{hash}", host, $"https://{host}/.well-known/lnurlp/alice",
            DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp().Map($"https://{host}/verify/{hash}", "{\"status\":\"ERROR\",\"reason\":\"nope\"}");
        var rx = Rx(host, http);

        var inv = await rx.GetInvoice(hash, TestContext.Current.CancellationToken);

        Assert.Null(inv);
        TrackedInvoiceRegistry.Remove(hash);
    }

    [Fact]
    public async Task CheckVerifySupport_flags_missing_verify()
    {
        var host = "nv.example";
        var cb = $"https://{host}/.well-known/lnurlp/alice/";
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayMeta.Replace("{CB}", cb))
            .Map($"{cb}?amount=1000", "{\"pr\":\"lnbc1\"}"); // invoice returned, but no verify field
        var rx = Rx(host, http);

        var err = await rx.CheckVerifySupport(TestContext.Current.CancellationToken);

        Assert.NotNull(err);
        Assert.Contains("verify", err);
    }

    [Fact]
    public async Task CheckVerifySupport_passes_when_verify_present()
    {
        var host = "yv.example";
        var cb = $"https://{host}/.well-known/lnurlp/alice/";
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayMeta.Replace("{CB}", cb))
            .Map($"{cb}?amount=1000", $"{{\"pr\":\"lnbc1\",\"verify\":\"{SparkVerify}\"}}");
        var rx = Rx(host, http);

        var err = await rx.CheckVerifySupport(TestContext.Current.CancellationToken);

        Assert.Null(err);
    }

    [Fact]
    public async Task GetInvoice_returns_paid_from_settled_cache_not_null()
    {
        // Simulate the poller having already settled + un-tracked this invoice. GetInvoice must still
        // report Paid (not null), or BTCPay's poll path (LightningListener.PollPayment) evicts it.
        var hash = new string('d', 64);
        var paid = new LightningInvoice { Id = hash, PaymentHash = hash, Status = LightningInvoiceStatus.Paid };
        TrackedInvoiceRegistry.MarkSettled(hash, paid, DateTimeOffset.UtcNow.AddMinutes(5));

        var rx = Rx("settledrx.example", new FakeHttp());
        var inv = await rx.GetInvoice(hash, TestContext.Current.CancellationToken);

        Assert.NotNull(inv);
        Assert.Equal(LightningInvoiceStatus.Paid, inv!.Status);
    }

    [Fact]
    public async Task CreateInvoice_rejects_amount_mismatch()
    {
        var host = "mm.example";
        var cb = $"https://{host}/.well-known/lnurlp/alice/";
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayMeta.Replace("{CB}", cb))
            // Request 100,000 msat but the callback returns the 250,000,000 msat spec bolt11 -> guard trips.
            .Map($"{cb}?amount=100000", $"{{\"pr\":\"{SpecBolt11}\",\"verify\":\"{SparkVerify}\"}}");
        var rx = Rx(host, http);

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            rx.CreateInvoice(LightMoney.MilliSatoshis(100_000), null, null, TestContext.Current.CancellationToken));
        Assert.Contains("requested", ex.Message);
    }

    [Fact]
    public async Task CreateInvoice_uses_trailing_slash_callback_and_tracks_verify_url()
    {
        // Pins the Blitz callback quirk: callback == lnurlp URL + trailing slash; the amount query is
        // appended cleanly and the LUD-21 verify URL from the callback response is what gets tracked.
        var host = "ok.example";
        var cb = $"https://{host}/.well-known/lnurlp/alice/";
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayMeta.Replace("{CB}", cb))
            .Map($"{cb}?amount=250000000", $"{{\"pr\":\"{SpecBolt11}\",\"verify\":\"{SparkVerify}\"}}");
        var rx = Rx(host, http);

        var inv = await rx.CreateInvoice(LightMoney.MilliSatoshis(250_000_000), null, null, TestContext.Current.CancellationToken);

        Assert.Equal(SpecBolt11, inv.BOLT11);
        Assert.Equal(LightningInvoiceStatus.Unpaid, inv.Status);
        Assert.Equal($"{cb}?amount=250000000", http.Requests[1]);
        Assert.True(TrackedInvoiceRegistry.TryGet(inv.PaymentHash, out var tracked));
        Assert.Equal(SparkVerify, tracked.VerifyUrl);
        TrackedInvoiceRegistry.Remove(inv.PaymentHash);
    }

    [Fact]
    public async Task CreateInvoice_rejects_unsafe_verify_url()
    {
        // A malicious/compromised LNURL server hands back a verify URL pointing at an internal
        // service — it must be refused at accept time, never tracked or polled.
        var host = "ssrf.example";
        var cb = $"https://{host}/.well-known/lnurlp/alice/";
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayMeta.Replace("{CB}", cb))
            .Map($"{cb}?amount=250000000", $"{{\"pr\":\"{SpecBolt11}\",\"verify\":\"http://169.254.169.254/latest/meta-data\"}}");
        var rx = Rx(host, http);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            rx.CreateInvoice(LightMoney.MilliSatoshis(250_000_000), null, null, TestContext.Current.CancellationToken));
        Assert.Contains("not an allowed destination", ex.Message);
    }

    [Fact]
    public async Task GetInvoice_drops_tracked_invoice_with_unsafe_verify_url()
    {
        // Defense in depth: an unsafe URL that somehow reached the registry (e.g. an old persisted
        // blob) is never polled — the invoice is dropped instead.
        var host = "ssrf2.example";
        var hash = new string('f', 64);
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", "http://127.0.0.1:8080/verify/x", host, $"https://{host}/.well-known/lnurlp/alice",
            DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp();
        var rx = Rx(host, http);

        var inv = await rx.GetInvoice(hash, TestContext.Current.CancellationToken);

        Assert.Null(inv);
        Assert.False(TrackedInvoiceRegistry.TryGet(hash, out _)); // removed, not left to poll forever
        Assert.Empty(http.Requests);                              // and never fetched
    }

    [Fact]
    public async Task CreateInvoice_truncates_comment_to_commentAllowed()
    {
        var host = "cm.example";
        var cb = $"https://{host}/.well-known/lnurlp/alice/";
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayMeta.Replace("{CB}", cb));
        var rx = Rx(host, http);
        var description = new string('x', 200); // over Blitz's commentAllowed of 150

        // The callback route is unmapped (404 -> throws), but the request it made is still recorded.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            rx.CreateInvoice(LightMoney.MilliSatoshis(250_000_000), description, null, TestContext.Current.CancellationToken));

        var expectedComment = Uri.EscapeDataString(new string('x', 150));
        Assert.Equal($"{cb}?amount=250000000&comment={expectedComment}", http.Requests[1]);
    }
}
