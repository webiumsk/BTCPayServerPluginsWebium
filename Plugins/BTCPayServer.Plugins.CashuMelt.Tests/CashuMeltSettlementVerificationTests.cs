using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.TestFixtures;
using Xunit;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

public class CashuMeltSettlementVerificationTests
{
    [Theory]
    [InlineData("PENDING")]
    [InlineData("FAILED")]
    [InlineData("MANUAL_REVIEW")]
    public void Unpaid_legacy_or_failed_quote_without_mint_expiry_can_resolve_only_after_monitoring(string state)
    {
        var (invoice, _) = LightningInvoiceFixture.Create(100);
        var request = new CashuMeltPaymentRequest { QuoteId = "q", Bolt11Invoice = invoice, AmountSats = 100, SettlementState = state };
        var quote = new MintQuoteBolt11Response("q", invoice, 100, "sat", "UNPAID", null, 0, 0);
        var now = DateTimeOffset.UtcNow.AddHours(2);
        var past = now.AddMinutes(-1);
        Assert.True(CashuMeltSettlementVerification.IsExpiredUnpaid(request, quote, "q", past, now));
        Assert.False(CashuMeltSettlementVerification.IsExpiredUnpaid(request, quote, "q", now.AddMinutes(1), now));
        Assert.False(CashuMeltSettlementVerification.IsExpiredUnpaid(request, quote, "q", DateTimeOffset.MinValue, DateTimeOffset.UtcNow));
        foreach (var invalid in new[] { quote with { State = "PAID" }, quote with { State = "ISSUED" },
                     quote with { AmountPaid = 100 }, quote with { AmountIssued = 100 }, quote with { Quote = "other" },
                     quote with { Request = null! }, quote with { Amount = 101 }, quote with { Unit = "usd" },
                     quote with { Expiry = now.AddMinutes(1).ToUnixTimeSeconds() } })
            Assert.False(CashuMeltSettlementVerification.IsExpiredUnpaid(request, invalid, "q", past, now));
        request.MeltQuoteId = "uncertain-payout";
        Assert.False(CashuMeltSettlementVerification.IsExpiredUnpaid(request, quote, "q", past, now));
        request.MeltQuoteId = null; request.MintIssuanceJson = "recoverable";
        Assert.False(CashuMeltSettlementVerification.IsExpiredUnpaid(request, quote, "q", past, now));
        request.MintIssuanceJson = null; request.QuoteExpiresAt = now.AddMinutes(1);
        Assert.False(CashuMeltSettlementVerification.IsExpiredUnpaid(request, quote, "q", past, now));
    }

    [Fact]
    public void Fee_buffer_ceiling_cannot_overflow_on_mint_controlled_amounts()
    {
        Assert.Equal(92233720368547759L, CashuMeltFeePolicy.EstimateFeeBufferSat(long.MaxValue));
    }

    [Fact]
    public void Confirmation_requires_the_exact_quote_amount_and_payment_preimage()
    {
        var (invoice, preimage) = LightningInvoiceFixture.Create(100);
        CashuMeltSettlementVerification.ValidateInvoice(invoice, 100);
        var paid = new MeltQuoteResponse("exact-quote", 100, 2, "PAID", null, preimage, null, invoice);
        Assert.True(CashuMeltSettlementVerification.IsPaidQuote(paid, "exact-quote", invoice));
        Assert.False(CashuMeltSettlementVerification.IsPaidQuote(paid, "other-quote", invoice));
        Assert.False(CashuMeltSettlementVerification.IsPaidQuote(paid with { Amount = 101 }, "exact-quote", invoice));
        Assert.False(CashuMeltSettlementVerification.IsPaidQuote(paid with { PaymentPreimage = new string('0', 64) }, "exact-quote", invoice));
        Assert.False(CashuMeltSettlementVerification.IsPaidQuote(paid with { State = "PENDING" }, "exact-quote", invoice));
        Assert.False(CashuMeltSettlementVerification.IsPaidQuote(null, "exact-quote", invoice));
    }

    [Fact]
    public void Modern_paid_amount_fields_are_authoritative_over_legacy_state()
    {
        var quote = new MintQuoteBolt11Response("q", "invoice", 100, "sat", "PAID", null, 0, 0);
        Assert.False(quote.HasBeenPaid);
        Assert.True((quote with { AmountPaid = 100 }).HasBeenPaid);
        Assert.True((quote with { AmountPaid = 100, AmountIssued = 100, State = null! }).HasBeenIssued);
    }
}
