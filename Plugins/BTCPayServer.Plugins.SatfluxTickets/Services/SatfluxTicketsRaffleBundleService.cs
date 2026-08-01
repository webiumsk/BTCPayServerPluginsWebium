#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Services.Integration;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>
/// Event-level raffle bundle allocation after ticket purchase (idempotent).
/// </summary>
public sealed class SatfluxTicketsRaffleBundleService
{
    private readonly RaffleEventBundleClientProvider _raffleBundleProvider;
    private readonly Logs _logs;

    public SatfluxTicketsRaffleBundleService(
        RaffleEventBundleClientProvider raffleBundleProvider,
        Logs logs)
    {
        _raffleBundleProvider = raffleBundleProvider;
        _logs = logs;
    }

    public async Task AllocateForOrderAsync(
        string storeId,
        Order order,
        Event ticketEvent,
        string baseUrl,
        InvoiceLogs? invoiceLogs = null)
    {
        if (order.PaymentStatus != TransactionStatus.Settled.ToString())
            return;

        if (ticketEvent.BundledRaffleTicketsPerAdmission <= 0 || ticketEvent.BundledRaffleId is not Guid raffleId)
        {
            Write(invoiceLogs,
                "No raffle bundle configured on this event.",
                InvoiceEventData.EventSeverity.Info);
            return;
        }

        var raffleBundle = _raffleBundleProvider.Client;
        if (raffleBundle is null)
        {
            Write(invoiceLogs,
                "Raffle bundle skipped: BTCPay Raffle plugin is not loaded or is older than 1.3.1",
                InvoiceEventData.EventSeverity.Warning);
            return;
        }

        var allocations = order.Tickets
            .Where(t => !string.IsNullOrWhiteSpace(t.Email))
            .GroupBy(t => NormalizeBuyerEmail(t.Email)!)
            .Select(g => new
            {
                Email = g.Key,
                Count = g.Count() * ticketEvent.BundledRaffleTicketsPerAdmission,
                BuyerName = BuildBuyerName(g.First())
            })
            .ToList();

        if (allocations.Count == 0)
        {
            Write(invoiceLogs,
                "Raffle bundle skipped: buyer email was missing on order tickets.",
                InvoiceEventData.EventSeverity.Info);
            return;
        }

        foreach (var alloc in allocations)
        {
            try
            {
                var bundleResult = await raffleBundle.AllocateForEventOrderAsync(
                    storeId,
                    raffleId,
                    alloc.Count,
                    alloc.Email,
                    alloc.BuyerName,
                    order.Id,
                    baseUrl);

                if (!bundleResult.Success)
                {
                    Write(invoiceLogs,
                        $"Raffle bundle failed for {MaskEmail(alloc.Email)}: {bundleResult.Error}",
                        InvoiceEventData.EventSeverity.Error);
                }
                else if (bundleResult.TicketsAllocated > 0)
                {
                    Write(invoiceLogs,
                        $"Allocated {bundleResult.TicketsAllocated} raffle ticket(s) for {MaskEmail(alloc.Email)}",
                        InvoiceEventData.EventSeverity.Success);
                }
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogWarning(ex,
                    "Raffle bundle failed for order {OrderId}",
                    order.Id);
                Write(invoiceLogs,
                    "Raffle bundle failed for this order. Check server logs for details.",
                    InvoiceEventData.EventSeverity.Error);
            }
        }
    }

    private static void Write(InvoiceLogs? logs, string message, InvoiceEventData.EventSeverity severity)
    {
        logs?.Write(message, severity);
    }

    private static string? NormalizeBuyerEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        return email.Trim().ToLowerInvariant();
    }

    private static string? BuildBuyerName(Ticket ticket)
    {
        var buyerName = $"{ticket.FirstName} {ticket.LastName}".Trim();
        return string.IsNullOrWhiteSpace(buyerName) ? null : buyerName;
    }

    private static string MaskEmail(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return "(none)";

        var at = address.IndexOf('@');
        if (at <= 0)
            return "****";

        return $"****{address[at..]}";
    }
}
