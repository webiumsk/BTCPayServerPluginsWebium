#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>
/// The single place that records a settled SEPA payment in BTCPay - mirrors
/// CashuMelt's TryRecordPaymentInBtcPayAsync: PaymentData(Settled) +
/// PaymentService.AddPayment + InvoiceEvent.ReceivedPayment, which makes the
/// InvoiceWatcher transition the invoice and fire webhooks. Prompt currency
/// equals invoice currency (EUR), so the rate is 1:1.
/// </summary>
public class SepaPaymentRecorder
{
    private readonly PaymentService _paymentService;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<SepaPaymentRecorder> _logger;

    public SepaPaymentRecorder(
        PaymentService paymentService,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        EventAggregator eventAggregator,
        ILogger<SepaPaymentRecorder> logger)
    {
        _paymentService = paymentService;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    public async Task<bool> RecordAsync(
        SepaPaymentRequest request,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(SepaInstantQrPlugin.SepaPaymentMethodId, out var handler))
        {
            _logger.LogWarning(
                "SEPA payment handler missing; cannot record payment for invoice {InvoiceId} reference {Reference}",
                request.InvoiceId, request.Reference);
            return false;
        }

        var invoice = await _invoiceRepository.GetInvoice(request.InvoiceId);
        if (invoice is null)
        {
            _logger.LogWarning(
                "Invoice {InvoiceId} not found while recording SEPA payment {Reference}",
                request.InvoiceId, request.Reference);
            return false;
        }

        var paymentData = new SepaPaymentData
        {
            Reference = request.Reference,
            Iban = request.Iban,
            Amount = amount,
        };

        var payment = new PaymentData
        {
            Id = request.Reference,
            InvoiceDataId = request.InvoiceId,
            Currency = request.Currency,
            Amount = amount,
            Status = PaymentStatus.Settled,
            Created = DateTimeOffset.UtcNow,
        };
        payment.Set(invoice, handler, paymentData);

        PaymentEntity? paymentEntity;
        try
        {
            paymentEntity = await _paymentService.AddPayment(payment, [request.Reference]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AddPayment threw for invoice {InvoiceId} reference {Reference}",
                request.InvoiceId, request.Reference);
            paymentEntity = null;
        }

        if (paymentEntity is null)
        {
            // AddPayment returns null when the payment already exists -
            // treat an existing identical payment as recorded (idempotency).
            var after = await _invoiceRepository.GetInvoice(request.InvoiceId);
            paymentEntity = after?.GetPayments(false)
                .FirstOrDefault(p =>
                    p.Id == request.Reference &&
                    p.PaymentMethodId == SepaInstantQrPlugin.SepaPaymentMethodId);
            if (paymentEntity is null)
                return false;
        }

        var updatedInvoice = await _invoiceRepository.GetInvoice(request.InvoiceId);
        if (updatedInvoice is not null)
        {
            _eventAggregator.Publish(new InvoiceEvent(updatedInvoice, InvoiceEvent.ReceivedPayment) { Payment = paymentEntity });
        }

        _logger.LogInformation(
            "sepa_payment_recorded invoice={InvoiceId} reference={Reference} amount={Amount} {Currency}",
            request.InvoiceId, request.Reference, amount, request.Currency);
        return true;
    }
}
