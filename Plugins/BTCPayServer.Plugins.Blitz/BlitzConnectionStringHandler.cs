#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
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
    private static readonly ConcurrentDictionary<string, (ResolvedBlitz Resolved, DateTimeOffset Expiry)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // Re-seed persisted tracked invoices exactly once, on the first Create — which BTCPay calls before any
    // GetInvoice (PollPayment does Create then GetInvoice). Doing it here (rather than in the poller's
    // StartAsync) guarantees the registry is re-armed before BTCPay's core startup poll can null-evict it.
    private static readonly object _loadLock = new();
    private static bool _loaded;

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
        var http = _httpClientFactory.CreateClient(nameof(BlitzConnectionStringHandler));
        // Bound each LNURL request rather than inheriting the default 100s HttpClient timeout.
        http.Timeout = TimeSpan.FromSeconds(30);

        ResolvedBlitz resolved;
        if (_cache.TryGetValue(normalized, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
        {
            resolved = cached.Resolved;
        }
        else
        {
            try
            {
                // Resolve (network) to validate the address up front; cached so the frequent per-poll
                // Create calls don't re-fetch. Failures are not cached, so they retry next time.
                resolved = BlitzResolver.Resolve(normalized, http, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
            _cache[normalized] = (resolved, DateTimeOffset.UtcNow.Add(CacheTtl));
        }

        return new BlitzLightningClient(resolved, network, http, _loggerFactory);
    }

    private void EnsurePersistedInvoicesLoaded()
    {
        if (_loaded || _settings is null) return;
        lock (_loadLock)
        {
            if (_loaded) return;
            try
            {
                new BlitzPersistence(_settings).LoadAsync().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _loggerFactory.CreateLogger(nameof(BlitzConnectionStringHandler))
                    .LogWarning(e, "Failed to load persisted Blitz tracked invoices");
            }
            _loaded = true;
        }
    }
}
