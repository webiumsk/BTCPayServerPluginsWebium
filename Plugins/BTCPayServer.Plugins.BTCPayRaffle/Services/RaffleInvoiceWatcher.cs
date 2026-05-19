#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>
/// Background service that listens for BTCPay invoice events.
/// When a Lightning invoice for ticket purchases is confirmed, ticket numbers are allocated automatically.
/// </summary>
public class RaffleInvoiceWatcher : EventHostedServiceBase
{
    private readonly InvoiceRepository _invoiceRepository;
    private readonly RaffleService _raffleService;
    private readonly RaffleTicketEmailService _ticketEmail;

    public RaffleInvoiceWatcher(
        EventAggregator eventAggregator,
        InvoiceRepository invoiceRepository,
        RaffleService raffleService,
        RaffleTicketEmailService ticketEmail,
        Logs logs) : base(eventAggregator, logs)
    {
        _invoiceRepository = invoiceRepository;
        _raffleService = raffleService;
        _ticketEmail = ticketEmail;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is not InvoiceEvent { Name: InvoiceEvent.Confirmed or InvoiceEvent.Completed } invoiceEvent)
            return;

        var invoice = await _invoiceRepository.GetInvoice(invoiceEvent.Invoice.Id);
        if (invoice?.Metadata?.PosData is null) return;

        RaffleInvoiceMeta? meta;
        try
        {
            meta = JsonSerializer.Deserialize<RaffleInvoiceMeta>(
                invoice.Metadata.PosData.ToString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return; // not a raffle invoice
        }

        if (meta?.RaffleId is null || meta.TicketCount <= 0) return;

        try
        {
            var (tickets, isNew) = await _raffleService.AllocateTicketsAsync(
                invoice.Id,
                meta.RaffleId.Value,
                meta.TicketCount,
                meta.BuyerEmail,
                meta.BuyerName);

            if (isNew && !string.IsNullOrEmpty(meta.BuyerEmail) && !string.IsNullOrEmpty(meta.BaseUrl))
            {
                var receiptUrl = $"{meta.BaseUrl.TrimEnd('/')}/raffle/receipt/{invoice.Id}";
                await _ticketEmail.SendTicketsEmailAsync(
                    meta.RaffleId.Value,
                    meta.RaffleName ?? "Raffle",
                    meta.BuyerEmail,
                    meta.BuyerName,
                    tickets,
                    meta.BaseUrl,
                    receiptUrl);
            }
        }
        catch (System.Exception ex)
        {
            Logs.PayServer.LogError(ex,
                "Failed to allocate raffle tickets (invoice={InvoiceId}, raffle={RaffleId})",
                invoiceEvent.Invoice.Id, meta.RaffleId);
        }
    }
}

/// <summary>Raffle-specific metadata embedded in the BTCPay invoice PosData field.</summary>
public record RaffleInvoiceMeta(
    System.Guid? RaffleId,
    int TicketCount,
    string? BuyerEmail,
    string? BuyerName,
    string? BaseUrl = null,
    string? RaffleName = null);
