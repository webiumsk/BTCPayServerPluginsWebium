using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public class LightningAddressResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LightningAddressResolver> _logger;

    public LightningAddressResolver(HttpClient httpClient, ILogger<LightningAddressResolver> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // Overload for manual instantiation (no DI logger available)
    public LightningAddressResolver(HttpClient httpClient)
        : this(httpClient, NullLogger<LightningAddressResolver>.Instance) { }

    /// <summary>
    /// Resolves a Lightning address to a BOLT11 invoice.
    /// If <paramref name="amountSats"/> is below the LNURL minimum, it is clamped up
    /// (as long as <paramref name="maxAmountSats"/> permits); if it exceeds the maximum
    /// it is clamped down. Returns the effective amount actually encoded in the invoice.
    /// </summary>
    public async Task<(string Bolt11, long EffectiveAmountSats)> ResolveInvoiceAsync(
        string lightningAddress,
        long amountSats,
        long maxAmountSats,
        CancellationToken cancellationToken = default)
    {
        var parts = lightningAddress.Split('@', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new InvalidOperationException("Invalid lightning address format. Expected user@domain.");

        var lnurlpUrl = $"https://{parts[1]}/.well-known/lnurlp/{parts[0]}";
        await using var lnurlpStream = await _httpClient.GetStreamAsync(lnurlpUrl, cancellationToken);
        var lnurlp = await JsonSerializer.DeserializeAsync<LnurlpResponse>(lnurlpStream, CaseInsensitive, cancellationToken)
                     ?? throw new InvalidOperationException("Invalid LNURL-pay metadata response (null).");

        // Check for LNURL error response ({"status":"ERROR","reason":"..."})
        if (string.Equals(lnurlp.Status, "ERROR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"LNURL-pay endpoint returned error: {lnurlp.Reason ?? "unknown"}");

        if (string.IsNullOrEmpty(lnurlp.Callback))
            throw new InvalidOperationException("LNURL-pay metadata missing callback URL.");

        // Convert limits from msat to sat (round min up, max down).
        // Guard against zero/negative values: missing fields default to 0 → treat as unlimited.
        long minSat = lnurlp.MinSendable > 0 ? (lnurlp.MinSendable + 999) / 1000 : 1;
        long maxSat = lnurlp.MaxSendable > 0 ? lnurlp.MaxSendable / 1000 : long.MaxValue / 2;

        _logger.LogDebug("LNURL {Addr}: minSendable={Min}msat maxSendable={Max}msat → minSat={MinSat} maxSat={MaxSat}",
            lightningAddress, lnurlp.MinSendable, lnurlp.MaxSendable, minSat, maxSat);

        // Clamp amount to [minSat, maxSat] using maxAmountSats as the ceiling
        long effectiveSat = Math.Max(amountSats, minSat);
        effectiveSat = Math.Min(effectiveSat, Math.Min(maxSat, maxAmountSats));

        if (effectiveSat < minSat)
            throw new InvalidOperationException(
                $"Available amount {maxAmountSats} sat is below the LNURL-pay minimum of {minSat} sat for {lightningAddress}.");

        var amountMsat = effectiveSat * 1000L;
        var callbackUrl = $"{lnurlp.Callback}{(lnurlp.Callback.Contains('?') ? "&" : "?")}amount={amountMsat}";
        await using var callbackStream = await _httpClient.GetStreamAsync(callbackUrl, cancellationToken);
        var callback = await JsonSerializer.DeserializeAsync<LnurlCallbackResponse>(callbackStream, CaseInsensitive, cancellationToken)
                       ?? throw new InvalidOperationException("Invalid LNURL callback response (null).");

        if (!string.IsNullOrEmpty(callback.Status) &&
            string.Equals(callback.Status, "ERROR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"LNURL callback returned error: {callback.Reason ?? "unknown"}");

        if (string.IsNullOrEmpty(callback.Pr))
            throw new InvalidOperationException("LNURL callback did not return a bolt11 invoice.");

        return (callback.Pr, effectiveSat);
    }

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private sealed record LnurlpResponse(
        string? Callback,
        long MinSendable,
        long MaxSendable,
        string? Status,
        string? Reason);

    private sealed record LnurlCallbackResponse(
        string? Pr,
        string? Status,
        string? Reason);
}
