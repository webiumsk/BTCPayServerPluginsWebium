using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>Bind recovered invoices to immutable local checkout intent before allowing reuse or fulfillment.</summary>
internal static class TicketSalesInvoiceRecovery
{
    internal const string CreationAttempted = "PublicInvoiceCreationAttempted";
    internal static bool Matches(InvoiceEntity invoice, Order order) => invoice != null && order != null
        && !string.IsNullOrEmpty(order.Id) && !string.IsNullOrEmpty(order.TxnId) && !string.IsNullOrEmpty(order.StoreId)
        && !string.IsNullOrEmpty(order.Currency) && order.TotalAmount > 0
        && invoice.StoreId == order.StoreId && invoice.Metadata?.OrderId == order.Id
        && invoice.Metadata.AdditionalData != null && invoice.Metadata.AdditionalData.TryGetValue("TxnId", out var txn)
        && txn != null && txn.Type == Newtonsoft.Json.Linq.JTokenType.String && (string)txn == order.TxnId
        && invoice.Price == order.TotalAmount && string.Equals(invoice.Currency, order.Currency, StringComparison.OrdinalIgnoreCase)
        && invoice.GetInternalTags(SimpleTicketSalesHostedService.TICKET_SALES_PREFIX).Any(tag => tag == order.TxnId);

    internal static InvoiceEntity Select(IEnumerable<InvoiceEntity> invoices, Order order)
    {
        var candidates = invoices.Where(i => i.GetInternalTags(SimpleTicketSalesHostedService.TICKET_SALES_PREFIX)
            .Any(tag => tag == order.TxnId)).ToArray();
        // A conflicting invoice with the same server-issued intent is an integrity error, not permission to bill again.
        if (candidates.Any(i => !Matches(i, order)))
            throw new InvalidOperationException("Existing ticket invoice does not match its checkout intent.");
        return candidates.OrderByDescending(i => i.Status is InvoiceStatus.Settled or InvoiceStatus.Processing)
            .ThenByDescending(i => i.InvoiceTime).ThenBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    internal static async Task<InvoiceEntity> FindOrCreateAsync(Order order,
        Func<Task<InvoiceEntity[]>> find, Func<Task> persistCreationAttempt, Func<Task<InvoiceEntity>> create)
    {
        // If lookup fails, propagate; never turn an unavailable recovery query into a second invoice.
        var existing = Select(await find(), order);
        if (existing != null) return existing;
        // An absent search result cannot prove an earlier, uncertain host commit will not complete later.
        if (order.InvoiceStatus == CreationAttempted) throw new TicketSalesInvoicePendingException();
        order.InvoiceStatus = CreationAttempted;
        await persistCreationAttempt();
        return await create();
    }
}

internal sealed class TicketSalesInvoicePendingException : InvalidOperationException
{
    internal TicketSalesInvoicePendingException() : base("Ticket invoice creation has already been attempted; lookup-only recovery is required.") { }
}
