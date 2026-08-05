#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Blitz;

public class BlitzConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISettingsRepository? _settings;

    // BTCPay's LightningClientFactory does NOT cache clients — it calls this handler's Create on every
    // poll/listen/operation (e.g. LightningListener.PollPayment). Resolving over the network each time
    // would hammer the LNURL server and block a thread per call, so cache the resolution briefly.
    // Expired entries are pruned opportunistically on access so removed connections don't accumulate.
    private static readonly ConcurrentDictionary<string, (ResolvedBlitz Resolved, DateTimeOffset Expiry)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // Single-flight per address: concurrent cache misses (BTCPay fires several Create calls at once on
    // startup/poll) share one network resolution instead of racing duplicates.
    private static readonly ConcurrentDictionary<string, Lazy<Task<ResolvedBlitz>>> _resolving = new();

    // Re-seed persisted tracked invoices on the first successful load, triggered from Create — which
    // BTCPay calls before any GetInvoice (PollPayment does Create then GetInvoice). Doing it here
    // (rather than in the poller's StartAsync) guarantees the registry is re-armed before BTCPay's core
    // startup poll can null-evict it. A failed load is retried (with backoff), not marked done.
    private static readonly object _loadLock = new();
    private static bool _loaded;
    private static DateTimeOffset _loadRetryAfter = DateTimeOffset.MinValue;
    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromSeconds(30);

    public BlitzConnectionStringHandler(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory,
        ISettingsRepository? settings = null)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _settings = settings;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "blitz")
        {
            error = null;
            return null;
        }

        if (!kv.TryGetValue("ln-address", out var lnAddress) || string.IsNullOrWhiteSpace(lnAddress))
        {
            error = "The key 'ln-address' (your Blitz Wallet Lightning address or username) is mandatory for blitz connection strings";
            return null;
        }

        EnsurePersistedInvoicesLoaded();

        error = null;
        var normalized = BlitzResolver.NormalizeAddress(lnAddress);
        var http = _httpClientFactory.CreateClient(BlitzHttp.ClientName);
        // Bound each LNURL request rather than inheriting the default 100s HttpClient timeout.
        http.Timeout = TimeSpan.FromSeconds(30);

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _cache)
            if (entry.Value.Expiry <= now)
                _cache.TryRemove(entry.Key, out _);

        ResolvedBlitz resolved;
        if (_cache.TryGetValue(normalized, out var cached) && cached.Expiry > now)
        {
            resolved = cached.Resolved;
        }
        else
        {
            // Resolve (network) to validate the address up front; cached so the frequent per-poll
            // Create calls don't re-fetch, and single-flighted so concurrent misses share one fetch.
            // Failures are not cached, so they retry next time.
            var flight = _resolving.GetOrAdd(normalized, key => new Lazy<Task<ResolvedBlitz>>(
                () => BlitzResolver.Resolve(key, http, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                resolved = flight.Value.GetAwaiter().GetResult();
                _cache[normalized] = (resolved, DateTimeOffset.UtcNow.Add(CacheTtl));
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
            finally
            {
                _resolving.TryRemove(normalized, out _);
            }
        }

        return new BlitzLightningClient(resolved, network, http, _loggerFactory);
    }

    private void EnsurePersistedInvoicesLoaded()
    {
        if (_loaded || _settings is null) return;
        lock (_loadLock)
        {
            if (_loaded || DateTimeOffset.UtcNow < _loadRetryAfter) return;
            try
            {
                new BlitzPersistence(_settings).LoadAsync().GetAwaiter().GetResult();
                _loaded = true;
            }
            catch (Exception e)
            {
                // A transient failure must not permanently disable restart recovery: leave _loaded
                // false and retry on a later Create, after a short backoff.
                _loadRetryAfter = DateTimeOffset.UtcNow.Add(LoadRetryDelay);
                _loggerFactory.CreateLogger(nameof(BlitzConnectionStringHandler))
                    .LogWarning(e, "Failed to load persisted Blitz tracked invoices; will retry");
            }
        }
    }
}
