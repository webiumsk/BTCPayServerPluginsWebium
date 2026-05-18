#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Emails.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

/// <summary>
/// Background service that listens for BTCPay invoice events.
/// When a Lightning invoice for ticket purchases is confirmed, ticket numbers are allocated automatically.
/// </summary>
public class RaffleInvoiceWatcher : EventHostedServiceBase
{
    private readonly InvoiceRepository _invoiceRepository;
    private readonly RaffleService _raffleService;
    private readonly EmailSenderFactory _emailSenderFactory;

    public RaffleInvoiceWatcher(
        EventAggregator eventAggregator,
        InvoiceRepository invoiceRepository,
        RaffleService raffleService,
        EmailSenderFactory emailSenderFactory,
        Logs logs) : base(eventAggregator, logs)
    {
        _invoiceRepository = invoiceRepository;
        _raffleService = raffleService;
        _emailSenderFactory = emailSenderFactory;
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
                await TrySendTicketEmailAsync(meta, tickets, invoice.Id);
            }
        }
        catch (Exception ex)
        {
            Logs.PayServer.LogError(ex,
                "Failed to allocate raffle tickets (invoice={InvoiceId}, raffle={RaffleId})",
                invoiceEvent.Invoice.Id, meta.RaffleId);
        }
    }

    private async Task TrySendTicketEmailAsync(RaffleInvoiceMeta meta, System.Collections.Generic.List<Data.Entities.RaffleTicket> tickets, string invoiceId)
    {
        try
        {
            var sender = await _emailSenderFactory.GetEmailSender();
            var settings = await sender.GetEmailSettings();
            if (settings?.IsComplete() != true) return;

            var receiptUrl = $"{meta.BaseUrl}/raffle/receipt/{invoiceId}";
            var raffleName = meta.RaffleName ?? "Raffle";
            var ticketNumbers = string.Join(", ", tickets.Select(t => $"#{t.TicketNumber}"));
            var subject = $"Your tickets — {raffleName}";
            var body = BuildEmailHtml(raffleName, ticketNumbers, tickets, receiptUrl, meta.BaseUrl!);

            sender.SendEmail(
                new MailboxAddress(meta.BuyerName ?? meta.BuyerEmail, meta.BuyerEmail!),
                subject,
                body);
        }
        catch (Exception ex)
        {
            Logs.PayServer.LogWarning(ex, "Could not send ticket email to {Email}", meta.BuyerEmail);
        }
    }

    private static string BuildEmailHtml(
        string raffleName, string ticketNumbers,
        System.Collections.Generic.List<Data.Entities.RaffleTicket> tickets,
        string receiptUrl, string baseUrl)
    {
        var sb = new StringBuilder();
        sb.Append($@"<!DOCTYPE html><html><body style=""font-family:sans-serif;max-width:600px;margin:0 auto;padding:20px"">
<div style=""background:#f8f9fa;border-radius:12px;padding:24px;text-align:center"">
  <div style=""font-size:48px"">🎟️</div>
  <h2 style=""margin:8px 0"">{System.Net.WebUtility.HtmlEncode(raffleName)}</h2>
  <p style=""color:#666"">Your ticket purchase was confirmed!</p>
</div>
<div style=""margin:24px 0"">
  <h3>Your ticket numbers:</h3>");

        foreach (var t in tickets)
        {
            var verifyUrl = $"{baseUrl}/raffle/ticket/{t.Id}";
            sb.Append($@"
  <div style=""border:2px solid #dee2e6;border-radius:8px;padding:12px 16px;margin:8px 0;display:flex;align-items:center;justify-content:space-between"">
    <span style=""font-size:28px;font-weight:900;font-family:monospace;color:#333"">#{t.TicketNumber}</span>
    <a href=""{verifyUrl}"" style=""font-size:12px;color:#0d6efd"">Verify</a>
  </div>");
        }

        sb.Append($@"
</div>
<div style=""text-align:center;margin:24px 0"">
  <a href=""{receiptUrl}"" style=""background:#0d6efd;color:#fff;text-decoration:none;padding:12px 28px;border-radius:8px;font-weight:600;display:inline-block"">
    View My Tickets
  </a>
</div>
<p style=""color:#999;font-size:12px;text-align:center"">Keep this email as proof of purchase. The receipt page also contains a QR code for each ticket.</p>
</body></html>");

        return sb.ToString();
    }
}

/// <summary>Raffle-specific metadata embedded in the BTCPay invoice PosData field.</summary>
public record RaffleInvoiceMeta(
    Guid? RaffleId,
    int TicketCount,
    string? BuyerEmail,
    string? BuyerName,
    string? BaseUrl = null,
    string? RaffleName = null);
