#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Flash;

/// <summary>
/// The receive side: creates invoices via the Flash LNURL-pay callback (capturing the LUD-21 verify
/// URL) and reads settlement via that verify URL. The verify-poll-and-build logic is static so the
/// shared poller can drive it without any per-connection state — a tracked invoice carries everything needed.
/// </summary>
public sealed class FlashReceiver
{
    private readonly ResolvedFlash _resolved;
    private readonly Network _network;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public FlashReceiver(ResolvedFlash resolved, Network network, HttpClient http, ILogger logger)
    { _resolved = resolved; _network = network; _http = http; _logger = logger; }

    private const string VerifyUnsupportedMessage =
        "This Lightning address's LNURL server does not support the LUD-21 'verify' extension, which is " +
        "required to detect payment settlement. Flash addresses (user@flashapp.me) support it.";

    /// <summary>
    /// Config-time probe: requests a minimal invoice from the pay callback and checks that the LUD-21
    /// verify field is present. Returns null when verify is supported, or a user-facing error message.
    /// (LUD-21 exposes verify only in the callback response, so this can't be checked from metadata alone.)
    /// </summary>
    public async Task<string?> CheckVerifySupport(CancellationToken ct)
    {
        JObject meta;
        try { meta = await FlashResolver.GetJson(_http, _resolved.PayEndpoint, ct); }
        catch (Exception e) { return e.Message; }

        var callback = meta["callback"]?.Value<string>();
        if (string.IsNullOrEmpty(callback)) return "The LNURL-pay endpoint is missing a callback URL.";
        var min = meta["minSendable"]?.Value<long>() ?? 1000;
        var max = meta["maxSendable"]?.Value<long>() ?? long.MaxValue;
        // Flash advertises minSendable of 1 msat; a sub-satoshi probe can be rejected by the
        // Lightning backend, so ask for at least 1 sat (clamped to the advertised maximum).
        var probeAmount = Math.Min(Math.Max(min, 1000), max);

        var cb = new UriBuilder(callback);
        var q = new StringBuilder(cb.Query.TrimStart('?'));
        if (q.Length > 0) q.Append('&');
        q.Append("amount=").Append(probeAmount);
        cb.Query = q.ToString();
        if (!FlashHttp.IsSafeUrl(cb.Uri, out var cbReason))
            return $"The LNURL callback URL is not an allowed destination: {cbReason}.";

        JObject json;
        try { json = await FlashResolver.GetJson(_http, cb.Uri, ct); }
        catch (Exception e) { return $"Could not request a probe invoice: {e.Message}"; }

        var verify = json["verify"]?.Value<string>();
        if (string.IsNullOrEmpty(verify) || !Uri.TryCreate(verify, UriKind.Absolute, out var verifyUri))
            return VerifyUnsupportedMessage;
        if (!FlashHttp.IsSafeUrl(verifyUri, out var vReason))
            return $"The LUD-21 verify URL is not an allowed destination: {vReason}.";
        return null;
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney? amount, string? description,
        CreateInvoiceParams? p, CancellationToken ct)
    {
        if (amount is null)
            throw new NotSupportedException(
                "LNURL requires an invoice amount; amountless/top-up invoices are not supported.");

        var meta = await FlashResolver.GetJson(_http, _resolved.PayEndpoint, ct);
        var callback = meta["callback"]?.Value<string>()
                       ?? throw new Exception("LNURL-pay response is missing a callback URL.");
        var min = meta["minSendable"]?.Value<long>() ?? 1;
        var max = meta["maxSendable"]?.Value<long>() ?? long.MaxValue;
        var msat = amount.MilliSatoshi;
        if (msat < min) throw new Exception($"Amount {msat} msat is below the minimum ({min} msat).");
        if (msat > max) throw new Exception($"Amount {msat} msat is above the maximum ({max} msat).");

        var cb = new UriBuilder(callback);
        var q = new StringBuilder(cb.Query.TrimStart('?'));
        if (q.Length > 0) q.Append('&');
        q.Append("amount=").Append(msat);
        var commentAllowed = meta["commentAllowed"]?.Value<int>() ?? 0;
        if (commentAllowed > 0 && !string.IsNullOrEmpty(description))
        {
            var c = description!.Length > commentAllowed ? description.Substring(0, commentAllowed) : description;
            q.Append("&comment=").Append(Uri.EscapeDataString(c));
        }
        cb.Query = q.ToString();
        if (!FlashHttp.IsSafeUrl(cb.Uri, out var cbReason))
            throw new Exception($"The LNURL callback URL is not an allowed destination: {cbReason}.");

        var json = await FlashResolver.GetJson(_http, cb.Uri, ct);
        var pr = json["pr"]?.Value<string>() ?? throw new Exception("LNURL callback did not return an invoice.");
        var bolt11 = BOLT11PaymentRequest.Parse(pr, _network);

        // Security guards against a malicious/broken LNURL server.
        if (bolt11.MinimumAmount != LightMoney.MilliSatoshis(msat))
            throw new Exception(
                $"LNURL returned an invoice for {bolt11.MinimumAmount.MilliSatoshi} msat but {msat} was requested.");
        if (p?.DescriptionHash is { } dh && dh != bolt11.DescriptionHash)
            throw new Exception("LNURL returned an invoice with a mismatched or missing description hash.");

        var paymentHash = bolt11.PaymentHash?.ToString() ?? throw new Exception("Invoice has no payment hash.");
        // LUD-21: the verify URL is returned by the callback. Without it we cannot detect settlement,
        // so fail loudly rather than track a guessed URL that would silently never confirm payment.
        var verifyUrl = json["verify"]?.Value<string>();
        if (string.IsNullOrEmpty(verifyUrl) || !Uri.TryCreate(verifyUrl, UriKind.Absolute, out var verifyUri))
            throw new NotSupportedException(VerifyUnsupportedMessage);
        // The verify URL is attacker-influenced input (remote JSON) that we will poll for hours and
        // persist across restarts — refuse anything but a public https destination (SSRF guard).
        if (!FlashHttp.IsSafeUrl(verifyUri, out var verifyReason))
            throw new NotSupportedException($"The LUD-21 verify URL is not an allowed destination: {verifyReason}.");
        var verifyHost = verifyUri.Host;

        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            paymentHash, pr, verifyUrl, verifyHost, _resolved.PayEndpoint.ToString(), bolt11.ExpiryDate,
            bolt11.MinimumAmount.MilliSatoshi));

        return new LightningInvoice
        {
            Id = paymentHash,
            PaymentHash = paymentHash,
            BOLT11 = pr,
            Amount = bolt11.MinimumAmount,
            Status = LightningInvoiceStatus.Unpaid,
            ExpiresAt = bolt11.ExpiryDate
        };
    }

    public Task<LightningInvoice?> GetInvoice(string paymentHash, CancellationToken ct)
    {
        // A recently-settled invoice must keep reporting Paid (never null) or BTCPay's poll path evicts it.
        if (TrackedInvoiceRegistry.TryGetSettled(paymentHash, out var paid))
            return Task.FromResult<LightningInvoice?>(paid);
        if (TrackedInvoiceRegistry.TryGet(paymentHash, out var t))
            return PollAndBuild(t, _http, ct);
        // MarkSettled writes _settled BEFORE removing from tracked, so a tracked-miss here means a
        // concurrent settle may have just completed — re-check settled to avoid returning null for a
        // just-settled invoice (which BTCPay would evict from monitoring).
        if (TrackedInvoiceRegistry.TryGetSettled(paymentHash, out paid))
            return Task.FromResult<LightningInvoice?>(paid);
        return Task.FromResult<LightningInvoice?>(null);
    }

    /// <summary>
    /// Polls a tracked invoice's LUD-21 verify URL and builds its status. Connection-agnostic:
    /// used both by GetInvoice and by the shared poller. Returns a minimal Unpaid invoice (never
    /// null) on a transient transport error so BTCPay/the poller keeps the invoice tracked.
    /// </summary>
    public static async Task<LightningInvoice?> PollAndBuild(TrackedInvoice t, HttpClient http, CancellationToken ct)
    {
        // Defense in depth for URLs re-armed from persistence (or any path that bypassed the
        // accept-time check): never poll a non-public destination; drop the invoice instead.
        if (!Uri.TryCreate(t.VerifyUrl, UriKind.Absolute, out var verifyUri) ||
            !FlashHttp.IsSafeUrl(verifyUri, out _))
        {
            TrackedInvoiceRegistry.Remove(t.PaymentHash);
            return null;
        }

        JObject? json = null;
        bool transportError = false;
        try
        {
            using var resp = await http.GetAsync(t.VerifyUrl, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) json = JObject.Parse(body);
            else transportError = true;
        }
        catch { transportError = true; }

        if (json?["status"]?.Value<string>()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
            return null; // genuine not-found

        if (json is null)
        {
            if (transportError)
                return new LightningInvoice
                { Id = t.PaymentHash, PaymentHash = t.PaymentHash, Status = LightningInvoiceStatus.Unpaid };
            return null;
        }

        var settled = json["settled"]?.Value<bool>() ?? false;
        var preimage = json["preimage"]?.Value<string>();
        return BuildInvoice(t, settled, preimage);
    }

    private static LightningInvoice BuildInvoice(TrackedInvoice t, bool settled, string? preimage)
    {
        // Amount was captured at creation — re-parsing the BOLT11 here could throw on a corrupted
        // tracked/persisted entry and take down the whole poll cycle for that invoice.
        var amount = t.AmountMsat > 0 ? LightMoney.MilliSatoshis(t.AmountMsat) : null;
        var status = settled ? LightningInvoiceStatus.Paid
            : t.ExpiresAt < DateTimeOffset.UtcNow ? LightningInvoiceStatus.Expired
            : LightningInvoiceStatus.Unpaid;
        string? valid = settled && preimage is not null && IsValidPreimage(preimage, t.PaymentHash) ? preimage : null;
        return new LightningInvoice
        {
            Id = t.PaymentHash,
            PaymentHash = t.PaymentHash,
            BOLT11 = t.Bolt11,
            Amount = amount,
            AmountReceived = settled ? amount : null,
            Status = status,
            Preimage = valid,
            PaidAt = settled ? DateTimeOffset.UtcNow : null,
            ExpiresAt = t.ExpiresAt
        };
    }

    public static bool IsValidPreimage(string? preimage, string paymentHash)
    {
        preimage = preimage?.Trim() ?? "";
        paymentHash = paymentHash.Trim().ToLowerInvariant();
        if (preimage.Length != 64 || paymentHash.Length != 64) return false;
        foreach (var c in preimage) if (!Uri.IsHexDigit(c)) return false;
        try
        {
            var bytes = Encoders.Hex.DecodeData(preimage);
            var computed = Encoders.Hex.EncodeData(Hashes.SHA256(bytes));
            return computed.Equals(paymentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

}
