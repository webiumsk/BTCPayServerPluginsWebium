using System;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

public class SepaPaymentRecorderIsolationTests
{
    private sealed class RecorderWithObservedInvoice : SepaPaymentRecorder
    {
        private readonly InvoiceEntity? _invoice;
        public int InvoiceLookups { get; private set; }

        // Null downstream dependencies are deliberate tripwires: an ownership
        // mismatch must return before touching a handler, payment service or event.
        public RecorderWithObservedInvoice(InvoiceEntity? invoice)
            : base(null!, null!, null!, null!, NullLogger<SepaPaymentRecorder>.Instance)
            => _invoice = invoice;

        protected override Task<InvoiceEntity?> GetInvoiceAsync(string invoiceId)
        {
            InvoiceLookups++;
            return Task.FromResult(_invoice);
        }
    }

    [Theory]
    [InlineData("store-b")]
    [InlineData("STORE-A")]
    [InlineData("")]
    public async Task Actual_recorder_rejects_an_invoice_owned_by_a_different_store_before_payment_side_effects(string invoiceStore)
    {
        var recorder = new RecorderWithObservedInvoice(new InvoiceEntity { StoreId = invoiceStore });
        var request = new SepaPaymentRequest
        {
            StoreId = "store-a", InvoiceId = "synthetic-invoice", Reference = "1234567890",
            Iban = "SK4811000000002944116480", AmountDue = 12.50m, Currency = "EUR",
        };

        Assert.False(await recorder.RecordAsync(request, 12.50m));
        Assert.Equal(1, recorder.InvoiceLookups);
    }

    [Fact]
    public async Task Missing_invoice_never_reaches_payment_dependencies()
    {
        var recorder = new RecorderWithObservedInvoice(null);
        Assert.False(await recorder.RecordAsync(new SepaPaymentRequest
        {
            StoreId = "store-a", InvoiceId = "missing-synthetic-invoice", Reference = "1234567890",
        }, 12.50m));
        Assert.Equal(1, recorder.InvoiceLookups);
    }
}
