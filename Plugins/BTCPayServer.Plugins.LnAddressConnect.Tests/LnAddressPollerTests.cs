using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LnAddressConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.LnAddressConnect.Tests;

public class LnAddressPollerTests
{
    [Fact]
    public async Task Poller_publishes_settled_and_prunes()
    {
        var host = "poll.example";
        var hash = new string('c', 64);
        var t = new TrackedInvoice(hash, "lnbcrt1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay",
            DateTimeOffset.UtcNow.AddHours(1));
        TrackedInvoiceRegistry.Add(t);

        // Return Paid only for THIS invoice, null for any other (so concurrent test-class invoices
        // in the shared static registry are left untouched).
        LnAddressPollerService.PollOverride = (ti, _) => Task.FromResult<LightningInvoice?>(
            ti.PaymentHash == hash
                ? new LightningInvoice { Id = ti.PaymentHash, PaymentHash = ti.PaymentHash, Status = LightningInvoiceStatus.Paid }
                : null);

        using var listener = new LnAddressListener(ti => ti.PaymentHash == hash);
        var waiter = listener.WaitInvoice(TestContext.Current.CancellationToken);

        var poller = new LnAddressPollerService(
            NullLogger<LnAddressPollerService>.Instance, new SimpleHttpClientFactory(), TimeSpan.FromMilliseconds(20));
        await poller.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var seen = await waiter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(hash, seen.PaymentHash);
            Assert.False(TrackedInvoiceRegistry.TryGet(hash, out _));
        }
        finally
        {
            await poller.StopAsync(TestContext.Current.CancellationToken);
            LnAddressPollerService.PollOverride = null;
        }
    }

    [Fact]
    public async Task Poller_settles_many_invoices_concurrently_without_races()
    {
        // 60 invoices across 4 hosts. PollOverride settles even-indexed and throws on odd-indexed, so the
        // concurrent success (MarkSettled) and error (backoff) paths run together under the concurrency
        // gate — the exact mix a non-thread-safe _backoff would corrupt.
        var hashes = new List<string>();
        for (int i = 0; i < 60; i++)
        {
            var hash = $"conc{i:D2}".PadRight(64, '0');
            hashes.Add(hash);
            var host = $"conc{i % 4}.example";
            TrackedInvoiceRegistry.Add(new TrackedInvoice(
                hash, "lnbcrt1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay",
                DateTimeOffset.UtcNow.AddHours(1)));
        }

        var ownHashes = new HashSet<string>(hashes);
        var inFlight = 0;
        var maxInFlight = 0;
        LnAddressPollerService.PollOverride = async (t, _) =>
        {
            // Foreign invoices from parallel test classes share the static registry - leave them alone.
            if (!ownHashes.Contains(t.PaymentHash))
                return null;

            var now = Interlocked.Increment(ref inFlight);
            try
            {
                // Record peak concurrency and yield so polls genuinely overlap
                // instead of completing synchronously one by one.
                int observed;
                do { observed = Volatile.Read(ref maxInFlight); }
                while (now > observed && Interlocked.CompareExchange(ref maxInFlight, now, observed) != observed);
                await Task.Delay(5);

                var idx = int.Parse(t.PaymentHash.Substring(4, 2));
                if (idx % 2 == 0)
                    return new LightningInvoice
                    { Id = t.PaymentHash, PaymentHash = t.PaymentHash, Status = LightningInvoiceStatus.Paid };
                throw new Exception("simulated poll failure");
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        };

        var settled = 0;
        void Handler(TrackedInvoice t, LightningInvoice inv)
        {
            // The Settled event is static and shared with parallel test classes - count only ours.
            if (ownHashes.Contains(t.PaymentHash))
                Interlocked.Increment(ref settled);
        }
        TrackedInvoiceRegistry.Settled += Handler;

        var poller = new LnAddressPollerService(
            NullLogger<LnAddressPollerService>.Instance, new SimpleHttpClientFactory(), TimeSpan.FromMilliseconds(10));
        await poller.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            for (int i = 0; i < 300 && Volatile.Read(ref settled) < 30; i++)
                await Task.Delay(25, TestContext.Current.CancellationToken);

            Assert.Equal(30, Volatile.Read(ref settled));                 // all 30 even invoices settled once
            // Polls overlapped, but never beyond the poller's per-cycle concurrency cap.
            Assert.True(Volatile.Read(ref maxInFlight) > 1, $"polls never ran concurrently (max {maxInFlight})");
            Assert.True(Volatile.Read(ref maxInFlight) <= 16, $"concurrency cap exceeded (max {maxInFlight})");
            foreach (var h in hashes)
                if (int.Parse(h.Substring(4, 2)) % 2 == 0)
                    Assert.True(TrackedInvoiceRegistry.TryGetSettled(h, out _)); // retrievable as Paid
        }
        finally
        {
            await poller.StopAsync(TestContext.Current.CancellationToken);
            LnAddressPollerService.PollOverride = null;
            TrackedInvoiceRegistry.Settled -= Handler;
            foreach (var h in hashes) TrackedInvoiceRegistry.Remove(h);
            // NOTE: don't PruneSettled() here — a global prune would wipe other (parallel) test classes'
            // settled entries. This test's settled hashes are unique (conc*) and expire via their grace.
        }
    }
}
