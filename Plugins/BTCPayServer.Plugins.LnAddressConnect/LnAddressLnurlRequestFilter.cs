#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Payments.LNURLPay;
using BTCPayServer.Services.Invoices;
using LNURL;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LnAddressConnect;

/// <summary>
/// Aligns BTCPay's served LNURL-pay parameters with LnAddress's when a store's BTC lightning backend is
/// a LnAddress Wallet lightning address.
///
/// Why this is required: for such a store the payable BOLT11 is minted by LnAddress's LNURL server (the
/// <see cref="LnAddressLightningClient"/> proxies it), and that invoice commits, via its BOLT11
/// <c>h</c> (description hash) tag, to <em>LnAddress's own</em> LNURL metadata. BTCPay by default serves
/// its <em>own</em> metadata (store name/description). LUD-06 requires the payer's wallet to check
/// that SHA256(served metadata) equals the invoice's <c>h</c> tag; the two differ, so strict wallets
/// (e.g. Phoenix, LnAddress itself) refuse to pay. By mirroring LnAddress's metadata here the hashes match
/// and the payment succeeds. This also corrects the advertised min/max sendable to LnAddress's real limits.
///
/// Tradeoff: the payer's wallet then shows LnAddress's identity line ("Pay to user@flashapp.me")
/// rather than the store description. This is unavoidable because the description hash is committed
/// by LnAddress.
/// </summary>
public class LnAddressLnurlRequestFilter : PluginHookFilter<LNURLPayRequest>
{
    public override string Hook => "modify-lnurlp-request";

    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LnAddressLnurlRequestFilter> _logger;

    public LnAddressLnurlRequestFilter(
        PaymentMethodHandlerDictionary handlers,
        IHttpClientFactory httpClientFactory,
        ILogger<LnAddressLnurlRequestFilter> logger)
    {
        _handlers = handlers;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public override async Task<LNURLPayRequest> Execute(LNURLPayRequest arg)
    {
        try
        {
            if (arg is not StoreLNURLPayRequest { Store: { } store })
                return arg;

            // Resolve the store's BTC lightning connection string and detect a LnAddress ln-address.
            var lnPmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var configs = store.GetPaymentMethodConfigs<LightningPaymentMethodConfig>(_handlers, onlyEnabled: true);
            if (!configs.TryGetValue(lnPmi, out var lnConfig))
                return arg;
            var connectionString = lnConfig.GetExternalLightningUrl();
            if (!TryGetLnAddressLnAddress(connectionString, out var lnAddress))
                return arg;

            var (username, domain) = LnAddressResolver.ParseLightningAddress(lnAddress!);
            var metadataUri = new Uri($"https://{domain}/.well-known/lnurlp/{Uri.EscapeDataString(username)}");
            // The domain comes from store configuration — apply the same SSRF policy as everywhere else
            // (this hook runs on every LNURL-pay request, so it must never become an internal-probe relay).
            if (!LnAddressHttp.IsSafeUrl(metadataUri, out _))
                return arg;

            using var httpClient = _httpClientFactory.CreateClient(LnAddressHttp.ClientName);
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var json = await FetchMetadataCached(httpClient, metadataUri, CancellationToken.None);
            if (json is null)
                return arg;

            ApplyLnAddressParameters(arg, json);
            return arg;
        }
        catch (Exception e)
        {
            // Never break checkout because of this enhancement; the payment may still work for
            // wallets that do not enforce the LUD-06 description-hash commitment.
            _logger.LogWarning(e, "Failed to align LNURL-pay parameters with LnAddress; leaving BTCPay defaults.");
            return arg;
        }
    }

    // The hook fires on every LNURL-pay request during checkout; a short-TTL cache keeps that from
    // turning into one outbound fetch per request while still tracking upstream metadata changes.
    private static readonly ConcurrentDictionary<string, (JObject Json, DateTimeOffset Expiry)> _metadataCache = new();
    private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromSeconds(60);

    // Per-URI single-flight: N concurrent checkout requests on a cold/expired entry share one
    // fetch instead of each firing its own GET (same pattern as the connection-string handler).
    private static readonly ConcurrentDictionary<string, Lazy<Task<JObject?>>> _metadataFetching = new();

    internal static async Task<JObject?> FetchMetadataCached(HttpClient http, Uri metadataUri, CancellationToken ct)
    {
        var key = metadataUri.ToString();
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _metadataCache)
            if (kv.Value.Expiry <= now)
                _metadataCache.TryRemove(kv.Key, out _);
        if (_metadataCache.TryGetValue(key, out var hit) && hit.Expiry > now)
            return hit.Json;

        var lazy = _metadataFetching.GetOrAdd(key, _ => new Lazy<Task<JObject?>>(async () =>
        {
            using var resp = await http.GetAsync(metadataUri, CancellationToken.None);
            if (!resp.IsSuccessStatusCode)
                return null;
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync(CancellationToken.None));
            _metadataCache[key] = (json, DateTimeOffset.UtcNow.Add(MetadataCacheTtl));
            return json;
        }));
        try
        {
            // The shared fetch is not cancelled by one caller's token; each caller only stops waiting.
            return await lazy.Value.WaitAsync(ct);
        }
        finally
        {
            _metadataFetching.TryRemove(key, out _);
        }
    }

    /// <summary>Detects a LnAddress connection string and extracts its lightning address (bare usernames
    /// expanded to the default domain). Mirrors <see cref="LnAddressConnectionStringHandler"/>.</summary>
    internal static bool TryGetLnAddressLnAddress(string? connectionString, out string? lnAddress)
    {
        lnAddress = null;
        if (string.IsNullOrEmpty(connectionString))
            return false;

        Dictionary<string, string> kv;
        try
        {
            kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            if (!LnAddressTypes.IsOurType(type))
                return false;

            if (!kv.TryGetValue("ln-address", out lnAddress) || string.IsNullOrWhiteSpace(lnAddress))
                return false;

            lnAddress = LnAddressResolver.NormalizeAddress(lnAddress, type);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Overwrites the served metadata with LnAddress's (so the served-metadata hash matches the invoice's
    /// committed description hash), narrows the sendable bounds to the intersection of BTCPay's and
    /// LnAddress's limits (only ever narrowed, never widened, so a fixed-amount invoice with min == max is
    /// preserved) and caps the allowed comment length to LnAddress's.
    /// </summary>
    internal static void ApplyLnAddressParameters(LNURLPayRequest arg, JObject flashMetadata)
    {
        var metadata = flashMetadata["metadata"]?.Value<string>();
        if (!string.IsNullOrEmpty(metadata))
            arg.Metadata = metadata;

        // Remote-supplied numbers: treat negative limits as absent rather than letting them poison
        // the served bounds or comment length.
        var flashMin = flashMetadata["minSendable"]?.Value<long>() is { } bmin and >= 0 ? new LightMoney(bmin) : null;
        var flashMax = flashMetadata["maxSendable"]?.Value<long>() is { } bmax and >= 0 ? new LightMoney(bmax) : null;

        // Compute the intersection of [BTCPay.Min, BTCPay.Max] and [LnAddress.Min, LnAddress.Max]. If the two
        // ranges are DISJOINT (LnAddress's min exceeds BTCPay's max, or vice versa) there is no valid
        // amount, so leave BTCPay's advertised bounds untouched rather than fabricating a fixed amount
        // LnAddress would reject anyway. The callback's own amount validation still rejects any
        // out-of-range amount cleanly, and disjoint ranges are not expected in practice.
        var newMin = Max(arg.MinSendable, flashMin);
        var newMax = Min(arg.MaxSendable, flashMax);
        if (newMin is null || newMax is null || newMin <= newMax)
        {
            if (newMin is not null)
                arg.MinSendable = newMin;
            if (newMax is not null)
                arg.MaxSendable = newMax;
        }

        if (flashMetadata["commentAllowed"]?.Value<int>() is { } flashComment and >= 0 &&
            arg.CommentAllowed > flashComment)
            arg.CommentAllowed = flashComment;
    }

    /// <summary>Returns the larger of two possibly-null amounts (null is treated as "no bound").</summary>
    private static LightMoney? Max(LightMoney? a, LightMoney? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a > b ? a : b;
    }

    /// <summary>Returns the smaller of two possibly-null amounts (null is treated as "no bound").</summary>
    private static LightMoney? Min(LightMoney? a, LightMoney? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a < b ? a : b;
    }
}
