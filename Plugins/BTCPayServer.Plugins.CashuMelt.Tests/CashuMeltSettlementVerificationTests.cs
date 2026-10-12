using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Plugins.TestFixtures;
using Xunit;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

public class CashuMeltSettlementVerificationTests
{
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
