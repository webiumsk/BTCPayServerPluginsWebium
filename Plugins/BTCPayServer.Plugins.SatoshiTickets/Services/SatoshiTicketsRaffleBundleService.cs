#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.SatoshiTickets.Data;
using BTCPayServer.Plugins.SatoshiTickets.Services.Integration;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SatoshiTickets.Services;

/// <summary>
/// Allocates BTCPay Raffle bundle tickets after event ticket purchase (idempotent).
/// </summary>
public sealed class SatoshiTicketsRaffleBundleService
{
    private readonly RaffleEventBundleClientProvider _raffleBundleProvider;
    private readonly Logs _logs;

    public SatoshiTicketsRaffleBundleService(
        RaffleEventBundleClientProvider raffleBundleProvider,
        Logs logs)
    {
        _raffleBundleProvider = raffleBundleProvider;
        _logs = logs;
    }

    public async Task AllocateForOrderAsync(
        string storeId,
        Order order,
        SimpleTicketSalesDbContext ctx,
        string baseUrl,
        InvoiceLogs? invoiceLogs = null)
    {
        if (order.PaymentStatus != TransactionStatus.Settled.ToString())
            return;

        var raffleBundle = _raffleBundleProvider.Client;
        if (raffleBundle is null)
        {
            Write(invoiceLogs,
                "Raffle bundle skipped: BTCPay Raffle plugin is not loaded or is older than 1.3.1",
                InvoiceEventData.EventSeverity.Warning);
            return;
        }

        var ticketTypesById = await ctx.TicketTypes
            .Where(t => t.EventId == order.EventId)
            .ToDictionaryAsync(t => t.Id);

        var allocations = order.Tickets
            .Where(t => !string.IsNullOrWhiteSpace(t.Email))
            .GroupBy(t => (Email: NormalizeBuyerEmail(t.Email)!, t.TicketTypeId))
            .Select(g =>
            {
                if (!ticketTypesById.TryGetValue(g.Key.TicketTypeId, out var tt))
                    return null;
                if (tt.BundledRaffleTicketsPerAdmission <= 0 || tt.BundledRaffleId is not Guid raffleId)
                    return null;
                return new
                {
                    g.Key.Email,
                    RaffleId = raffleId,
                    Count = g.Count() * tt.BundledRaffleTicketsPerAdmission,
                    BuyerName = BuildBuyerName(g.First())
                };
            })
            .Where(x => x != null)
            .GroupBy(x => (x!.Email, x.RaffleId))
            .Select(g => new
            {
                g.Key.Email,
                g.Key.RaffleId,
                Total = g.Sum(x => x!.Count),
                BuyerName = g.Select(x => x!.BuyerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            })
            .ToList();

        if (allocations.Count == 0)
        {
            Write(invoiceLogs,
                "No raffle bundle on purchased ticket tier(s), or buyer email was missing.",
                InvoiceEventData.EventSeverity.Info);
            return;
        }

        foreach (var alloc in allocations)
        {
            try
            {
                var eventOrderId = $"{order.Id}:{alloc.RaffleId:N}";
                var bundleResult = await raffleBundle.AllocateForEventOrderAsync(
                    storeId,
                    alloc.RaffleId,
                    alloc.Total,
                    alloc.Email,
                    alloc.BuyerName,
                    eventOrderId,
                    baseUrl);

                if (!bundleResult.Success)
                {
                    Write(invoiceLogs,
                        $"Raffle bundle failed for {alloc.Email}: {bundleResult.Error}",
                        InvoiceEventData.EventSeverity.Error);
                }
                else if (bundleResult.TicketsAllocated > 0)
                {
                    Write(invoiceLogs,
                        $"Allocated {bundleResult.TicketsAllocated} raffle ticket(s) for {alloc.Email}",
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
}
