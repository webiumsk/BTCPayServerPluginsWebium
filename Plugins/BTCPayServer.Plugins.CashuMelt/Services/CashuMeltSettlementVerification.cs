using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using NBitcoin;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public static class CashuMeltSettlementVerification
{
    public static void ValidateInvoice(string bolt11, long amountSats, bool requireUnexpired = true)
    {
        var invoice = BOLT11PaymentRequest.Parse(bolt11, Network.Main);
        if (invoice.MinimumAmount != LightMoney.Satoshis(amountSats))
            throw new InvalidOperationException("Lightning invoice amount does not match the requested payout");
        if (requireUnexpired && invoice.ExpiryDate <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Lightning invoice has expired");
    }

    /// <summary>Proves funding is unpaid after both the Lightning/quote expiry and BTCPay monitoring.</summary>
    internal static bool IsExpiredUnpaid(CashuMeltPaymentRequest request, MintQuoteBolt11Response quote,
        string mintQuoteId, DateTimeOffset monitoringExpiration, DateTimeOffset now)
    {
        if (quote.Quote != mintQuoteId || quote.Request != request.Bolt11Invoice
            || quote.Amount != request.AmountSats || quote.Unit != request.Unit
            || quote.State != "UNPAID" || quote.HasBeenPaid || quote.HasBeenIssued
            || request.MintedProofsJson != null || request.MintIssuanceJson != null
            || request.BlankOutputsJson != null || request.MeltQuoteId != null
            || monitoringExpiration >= now) return false;
        try
        {
            ValidateInvoice(request.Bolt11Invoice!, request.AmountSats, requireUnexpired: false);
            var expiry = BOLT11PaymentRequest.Parse(request.Bolt11Invoice!, Network.Main).ExpiryDate;
            // A missing mint expiry does not erase the signed Lightning invoice expiry.
            return expiry < now && (request.QuoteExpiresAt is null || request.QuoteExpiresAt < now)
                && (quote.Expiry is not > 0 || DateTimeOffset.FromUnixTimeSeconds(quote.Expiry.Value) < now);
        }
        catch { return false; }
    }

    public static bool ValidPreimage(string? preimage, string? bolt11)
    {
        // A null preimage is allowed only alongside an exact PAID mint quote, never inferred from spent proofs.
        if (string.IsNullOrEmpty(bolt11)) return false;
        try
        {
            var invoice = BOLT11PaymentRequest.Parse(bolt11, Network.Main);
            if (preimage is null) return true;
            if (preimage.Length != 64) return false;
            var hash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(preimage)));
            return hash.Equals(invoice.PaymentHash?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsPaidQuote(MeltQuoteResponse? quote, string expectedQuoteId, string? bolt11)
    {
        if (quote?.Quote != expectedQuoteId || !string.Equals(quote.State, "PAID", StringComparison.OrdinalIgnoreCase)
            || !ValidPreimage(quote.PaymentPreimage, bolt11)) return false;
        try
        {
            ValidateInvoice(bolt11!, quote.Amount, requireUnexpired: false);
            return quote.Request is null || quote.Request == bolt11;
        }
        catch { return false; }
    }
}
