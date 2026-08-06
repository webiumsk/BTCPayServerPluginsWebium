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

namespace BTCPayServer.Plugins.LnAddressConnect;

public class LnAddressConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISettingsRepository? _settings;

    // BTCPay's LightningClientFactory does NOT cache clients — it calls this handler's Create on every
    // poll/listen/operation (e.g. LightningListener.PollPayment). Resolving over the network each time
    // would hammer the LNURL server and block a thread per call, so cache the resolution briefly.
    // Expired entries are pruned opportunistically on access so removed connections don't accumulate.
    private static readonly ConcurrentDictionary<string, (ResolvedLnAddress Resolved, DateTimeOffset Expiry)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // Single-flight per address: concurrent cache misses (BTCPay fires several Create calls at once on
    // startup/poll) share one network resolution instead of racing duplicates.
    private static readonly ConcurrentDictionary<string, Lazy<Task<ResolvedLnAddress>>> _resolving = new();

    // Re-seed persisted tracked invoices on the first successful load, triggered from Create — which
    // BTCPay calls before any GetInvoice (PollPayment does Create then GetInvoice). Doing it here
    // (rather than in the poller's StartAsync) guarantees the registry is re-armed before BTCPay's core
    // startup poll can null-evict it. A failed load is retried (with backoff), not marked done.
    private static readonly object _loadLock = new();
    private static bool _loaded;
    private static bool _loadInProgress;
    private static DateTimeOffset _loadRetryAfter = DateTimeOffset.MinValue;
    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromSeconds(30);

    public LnAddressConnectionStringHandler(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory,
        ISettingsRepository? settings = null)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _settings = settings;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (!LnAddressTypes.IsOurType(type))
        {
            error = null;
            return null;
        }

        if (!kv.TryGetValue("ln-address", out var lnAddress) || string.IsNullOrWhiteSpace(lnAddress))
        {
            error = "The key 'ln-address' (your wallet's Lightning address) is mandatory for lnaddress connection strings";
            return null;
        }

        EnsurePersistedInvoicesLoaded();

        string normalized;
        try
        {
            // Legacy types (blitz/flash) expand bare usernames to their historical domain;
            // type=lnaddress requires a full user@domain address.
            normalized = LnAddressResolver.NormalizeAddress(lnAddress, type);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }

        error = null;
        var http = _httpClientFactory.CreateClient(LnAddressHttp.ClientName);
        // Bound each LNURL request rather than inheriting the default 100s HttpClient timeout.
        http.Timeout = TimeSpan.FromSeconds(30);

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _cache)
            if (entry.Value.Expiry <= now)
                _cache.TryRemove(entry.Key, out _);

        ResolvedLnAddress resolved;
        if (_cache.TryGetValue(normalized, out var cached) && cached.Expiry > now)
        {
            resolved = cached.Resolved;
        }
        else
        {
            // Resolve (network) to validate the address up front; cached so the frequent per-poll
            // Create calls don't re-fetch, and single-flighted so concurrent misses share one fetch.
            // Failures are not cached, so they retry next time.
            var flight = _resolving.GetOrAdd(normalized, key => new Lazy<Task<ResolvedLnAddress>>(
                () => LnAddressResolver.Resolve(key, http, CancellationToken.None),
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

        return new LnAddressLightningClient(resolved, network, http, _loggerFactory);
    }

    private void EnsurePersistedInvoicesLoaded()
    {
        if (_loaded || _settings is null) return;
        lock (_loadLock)
        {
            if (_loaded || _loadInProgress || DateTimeOffset.UtcNow < _loadRetryAfter) return;
            _loadInProgress = true;
        }

        // The settings round-trip runs outside the lock: one caller loads while later Create
        // calls observe _loadInProgress and return immediately instead of queueing behind it.
        try
        {
            new LnAddressPersistence(_settings).LoadAsync().GetAwaiter().GetResult();
            lock (_loadLock) { _loaded = true; _loadInProgress = false; }
        }
        catch (Exception e)
        {
            lock (_loadLock)
            {
                _loadRetryAfter = DateTimeOffset.UtcNow.Add(LoadRetryDelay);
                _loadInProgress = false;
            }

            // A transient failure must not permanently disable restart recovery: leave _loaded
            // false and retry on a later Create, after a short backoff.
            _loggerFactory.CreateLogger(nameof(LnAddressConnectionStringHandler))
                .LogWarning(e, "Failed to load persisted LnAddress tracked invoices; will retry");
        }
    }
}
