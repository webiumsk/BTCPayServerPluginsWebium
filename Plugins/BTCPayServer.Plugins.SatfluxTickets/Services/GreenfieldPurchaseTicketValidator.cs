#nullable enable
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

internal static class GreenfieldPurchaseTicketValidator
{
    public static IActionResult? Validate(
        ControllerBase controller,
        Dictionary<string, TicketType> ticketTypes,
        PurchaseTicketItemRequest[] items,
        Event ticketEvent,
        int settledTicketsSold)
    {
        var totalByType = new Dictionary<string, int>();

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.TicketTypeId))
            {
                return controller.CreateAPIError(422, "invalid-ticket-type", "TicketTypeId is required for each item");
            }

            if (item.Quantity <= 0)
            {
                return controller.CreateAPIError(422, "invalid-quantity", "Quantity must be greater than zero for each item");
            }

            if (item.Recipients == null || item.Recipients.Length != item.Quantity)
            {
                return controller.CreateAPIError(422, "recipients-count-mismatch",
                    $"Recipients count must equal quantity ({item.Quantity}) for ticket type {item.TicketTypeId}");
            }

            totalByType[item.TicketTypeId] = totalByType.GetValueOrDefault(item.TicketTypeId) + item.Quantity;

            foreach (var recipient in item.Recipients)
            {
                if (string.IsNullOrWhiteSpace(recipient?.Email))
                    return controller.CreateAPIError(422, "invalid-email", "Email is required for each recipient");
            }
        }

        var totalRequested = totalByType.Values.Sum();
        if (ticketEvent.HasMaximumCapacity && ticketEvent.MaximumEventCapacity.HasValue
            && settledTicketsSold + totalRequested > ticketEvent.MaximumEventCapacity.Value)
        {
            return controller.CreateAPIError(422, "event-capacity-reached", "The event has reached maximum capacity");
        }

        foreach (var (ticketTypeId, totalRequestedForType) in totalByType)
        {
            if (!ticketTypes.TryGetValue(ticketTypeId, out var ticketType))
            {
                return controller.CreateAPIError(404, "ticket-type-not-found",
                    $"Ticket type {ticketTypeId} was not found");
            }

            if (ticketType.TicketTypeState == EntityState.Disabled)
            {
                return controller.CreateAPIError(422, "ticket-type-not-active",
                    $"Ticket type {ticketType.Name} is not active");
            }

            var available = ticketType.Quantity - ticketType.QuantitySold;
            if (ticketType.Quantity > 0 && available < totalRequestedForType)
            {
                return controller.CreateAPIError(422, "insufficient-quantity",
                    $"Insufficient quantity for ticket type {ticketType.Name}. Available: {available}, requested: {totalRequestedForType}");
            }
        }

        return null;
    }
}
