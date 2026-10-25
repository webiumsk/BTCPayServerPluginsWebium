using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.ViewModels;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>Public checkout accepts identities and quantities, never client prices or names as authority.</summary>
internal static class TicketSalesValidation
{
    internal const int MaximumPerType = 10;
    internal const int MaximumAdmissions = 100;
    internal const int MaximumTypes = 200;

    internal static bool HasBoundedShape(TicketSalesSubmission draft) => draft != null
        && (draft.TxnId?.Length ?? 0) <= 32 && draft.Tickets != null && draft.Tickets.Count <= MaximumTypes
        && draft.ContactInfo != null && draft.ContactInfo.Count <= MaximumAdmissions
        && (draft.SharedEmail?.Length ?? 0) <= 254 && (draft.DeliveryOption?.Length ?? 0) <= 20
        && draft.Tickets.All(t => t != null && (t.TicketTypeId?.Length ?? 0) <= 128)
        && draft.ContactInfo.All(c => c != null && (c.TicketTypeId?.Length ?? 0) <= 128
            && (c.FirstName?.Length ?? 0) <= 100 && (c.LastName?.Length ?? 0) <= 100 && (c.Email?.Length ?? 0) <= 254);

    internal static string ValidateSelection(Event ev, IReadOnlyCollection<TicketType> available,
        List<TicketSelectionViewModel> requested, int settledCount, out List<TicketSelectionViewModel> canonical)
    {
        canonical = new();
        if (requested is null || requested.Count == 0 || requested.Count > MaximumTypes)
            return "SelectionChanged";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var types = available.Where(t => t.EventId == ev.Id && t.TicketTypeState == EntityState.Active)
            .ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var item in requested)
        {
            if (item is null || string.IsNullOrEmpty(item.TicketTypeId) || !seen.Add(item.TicketTypeId)
                || item.Quantity < 0 || item.Quantity > MaximumPerType)
                return "SelectionChanged";
            if (item.Quantity == 0) continue;
            if (!types.TryGetValue(item.TicketTypeId, out var type) || type.Price < 0)
                return "SelectionChanged";
            // A zero quantity on an uncapped event means unlimited, matching the merchant API contract.
            if ((ev.HasMaximumCapacity || type.Quantity > 0) && type.Quantity - type.QuantitySold < item.Quantity)
                return "SelectionChanged";
            canonical.Add(new TicketSelectionViewModel
            {
                TicketTypeId = type.Id, TicketTypeName = type.Name, Price = type.Price, Quantity = item.Quantity
            });
        }
        var count = canonical.Sum(t => t.Quantity);
        if (count == 0) return "ChooseFirst";
        if (count > MaximumAdmissions || (ev.HasMaximumCapacity && (!ev.MaximumEventCapacity.HasValue
            || (long)settledCount + count > ev.MaximumEventCapacity.Value)))
            return "SelectionChanged";
        decimal total;
        try { total = canonical.Sum(t => t.Price * t.Quantity); }
        catch (OverflowException) { return "SelectionChanged"; }
        if (total <= 0) return "ZeroPrice";
        if (total > ulong.MaxValue) return "SelectionChanged";
        return null;
    }

    internal static string ValidateRecipients(List<TicketSelectionViewModel> selected,
        List<TicketSalesContactViewModel> contacts, string delivery, string sharedEmail,
        out List<TicketContactInfoViewModel> canonical)
    {
        canonical = new();
        if (selected is null || contacts is null || contacts.Count != selected.Sum(t => t.Quantity)
            || contacts.Count > MaximumAdmissions || (delivery != "together" && delivery != "individual"))
            return "ContactsInvalid";
        var byAdmission = new Dictionary<(string, int), TicketSalesContactViewModel>();
        foreach (var contact in contacts)
        {
            if (contact is null || string.IsNullOrEmpty(contact.TicketTypeId)
                || !byAdmission.TryAdd((contact.TicketTypeId, contact.AdmissionIndex), contact))
                return "ContactsInvalid";
        }
        foreach (var type in selected)
        for (var i = 0; i < type.Quantity; i++)
        {
            if (!byAdmission.TryGetValue((type.TicketTypeId, i), out var contact)) return "ContactsInvalid";
            var first = contact.FirstName?.Trim();
            var last = contact.LastName?.Trim();
            var email = (delivery == "together" ? sharedEmail : contact.Email)?.Trim();
            if (string.IsNullOrWhiteSpace(first) || first.Length > 100 || string.IsNullOrWhiteSpace(last)
                || last.Length > 100 || string.IsNullOrWhiteSpace(email) || email.Length > 254
                || email.IndexOfAny(new[] { '\r', '\n' }) >= 0 || !new EmailAddressAttribute().IsValid(email))
                return "ContactsInvalid";
            canonical.Add(new TicketContactInfoViewModel
            {
                TicketTypeId = type.TicketTypeId, TicketTypeName = type.TicketTypeName,
                Quantity = 1, FirstName = first, LastName = last, Email = email
            });
        }
        return null;
    }

    internal static List<TicketSalesContactViewModel> MapLegacyContacts(TicketOrderViewModel order)
    {
        var result = new List<TicketSalesContactViewModel>();
        var position = 0;
        foreach (var item in order.Tickets)
        for (var i = 0; i < item.Quantity; i++)
        {
            var contact = order.ContactInfo?.ElementAtOrDefault(position++);
            result.Add(new TicketSalesContactViewModel
            {
                TicketTypeId = item.TicketTypeId, AdmissionIndex = i,
                FirstName = contact?.FirstName, LastName = contact?.LastName, Email = contact?.Email
            });
        }
        return result;
    }
}
