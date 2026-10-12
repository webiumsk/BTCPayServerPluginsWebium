using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using BTCPayServer.Lightning;
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
