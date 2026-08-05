#nullable enable
using System;
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

namespace BTCPayServer.Plugins.Blitz;

/// <summary>
/// Aligns BTCPay's served LNURL-pay parameters with Blitz's when a store's BTC lightning backend is
/// a Blitz Wallet lightning address.
///
/// Why this is required: for such a store the payable BOLT11 is minted by Blitz's LNURL server (the
/// <see cref="BlitzLightningClient"/> proxies it), and that invoice commits, via its BOLT11
/// <c>h</c> (description hash) tag, to <em>Blitz's own</em> LNURL metadata. BTCPay by default serves
/// its <em>own</em> metadata (store name/description). LUD-06 requires the payer's wallet to check
/// that SHA256(served metadata) equals the invoice's <c>h</c> tag; the two differ, so strict wallets
/// (e.g. Phoenix, Blitz itself) refuse to pay. By mirroring Blitz's metadata here the hashes match
/// and the payment succeeds. This also corrects the advertised min/max sendable to Blitz's real limits.
///
/// Tradeoff: the payer's wallet then shows Blitz's identity line ("Pay to user@blitzwalletapp.com")
/// rather than the store description. This is unavoidable because the description hash is committed
/// by Blitz.
/// </summary>
public class BlitzLnurlRequestFilter : PluginHookFilter<LNURLPayRequest>
{
    public override string Hook => "modify-lnurlp-request";

    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BlitzLnurlRequestFilter> _logger;

    public BlitzLnurlRequestFilter(
        PaymentMethodHandlerDictionary handlers,
        IHttpClientFactory httpClientFactory,
        ILogger<BlitzLnurlRequestFilter> logger)
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

            // Resolve the store's BTC lightning connection string and detect a Blitz ln-address.
            var lnPmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var configs = store.GetPaymentMethodConfigs<LightningPaymentMethodConfig>(_handlers, onlyEnabled: true);
            if (!configs.TryGetValue(lnPmi, out var lnConfig))
                return arg;
            var connectionString = lnConfig.GetExternalLightningUrl();
            if (!TryGetBlitzLnAddress(connectionString, out var lnAddress))
                return arg;

            var (username, domain) = BlitzResolver.ParseLightningAddress(lnAddress!);
            var metadataUri = new Uri($"https://{domain}/.well-known/lnurlp/{Uri.EscapeDataString(username)}");

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await httpClient.GetAsync(metadataUri, CancellationToken.None);
            if (!resp.IsSuccessStatusCode)
                return arg;
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());

            ApplyBlitzParameters(arg, json);
            return arg;
        }
        catch (Exception e)
        {
            // Never break checkout because of this enhancement; the payment may still work for
            // wallets that do not enforce the LUD-06 description-hash commitment.
            _logger.LogWarning(e, "Failed to align LNURL-pay parameters with Blitz; leaving BTCPay defaults.");
            return arg;
        }
    }

    /// <summary>Detects a Blitz connection string and extracts its lightning address (bare usernames
    /// expanded to the default domain). Mirrors <see cref="BlitzConnectionStringHandler"/>.</summary>
    internal static bool TryGetBlitzLnAddress(string? connectionString, out string? lnAddress)
    {
        lnAddress = null;
        if (string.IsNullOrEmpty(connectionString))
            return false;

        Dictionary<string, string> kv;
        try
        {
            kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            if (type != "blitz")
                return false;
        }
        catch
        {
            return false;
        }

        if (!kv.TryGetValue("ln-address", out lnAddress) || string.IsNullOrWhiteSpace(lnAddress))
            return false;

        lnAddress = BlitzResolver.NormalizeAddress(lnAddress);
        return true;
    }

    /// <summary>
    /// Overwrites the served metadata with Blitz's (so the served-metadata hash matches the invoice's
    /// committed description hash), narrows the sendable bounds to the intersection of BTCPay's and
    /// Blitz's limits (only ever narrowed, never widened, so a fixed-amount invoice with min == max is
    /// preserved) and caps the allowed comment length to Blitz's.
    /// </summary>
    internal static void ApplyBlitzParameters(LNURLPayRequest arg, JObject blitzMetadata)
    {
        var metadata = blitzMetadata["metadata"]?.Value<string>();
        if (!string.IsNullOrEmpty(metadata))
            arg.Metadata = metadata;

        var blitzMin = blitzMetadata["minSendable"]?.Value<long>() is { } bmin ? new LightMoney(bmin) : null;
        var blitzMax = blitzMetadata["maxSendable"]?.Value<long>() is { } bmax ? new LightMoney(bmax) : null;

        // Compute the intersection of [BTCPay.Min, BTCPay.Max] and [Blitz.Min, Blitz.Max]. If the two
        // ranges are DISJOINT (Blitz's min exceeds BTCPay's max, or vice versa) there is no valid
        // amount, so leave BTCPay's advertised bounds untouched rather than fabricating a fixed amount
        // Blitz would reject anyway. The callback's own amount validation still rejects any
        // out-of-range amount cleanly, and disjoint ranges are not expected in practice.
        var newMin = Max(arg.MinSendable, blitzMin);
        var newMax = Min(arg.MaxSendable, blitzMax);
        if (newMin is null || newMax is null || newMin <= newMax)
        {
            if (newMin is not null)
                arg.MinSendable = newMin;
            if (newMax is not null)
                arg.MaxSendable = newMax;
        }

        if (blitzMetadata["commentAllowed"]?.Value<int>() is { } blitzComment &&
            arg.CommentAllowed > blitzComment)
            arg.CommentAllowed = blitzComment;
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
