using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Emails.Services;
using BTCPayServer.Plugins.SatoshiTickets.Data;
using BTCPayServer.Plugins.SatoshiTickets.Models.Api;
using BTCPayServer.Plugins.SatoshiTickets.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SatoshiTickets.Controllers;

[Route("~/api/v1/stores/{storeId}/satoshi-tickets/events/{eventId}/")]
[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield, Policy = Policies.CanModifyStoreSettings)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldSatoshiTicketsController(
    EmailService emailService,
    SimpleTicketSalesDbContextFactory dbContextFactory,
    EmailSenderFactory emailSenderFactory,
    InvoiceRepository invoiceRepository,
    UIInvoiceController invoiceController,
    LinkGenerator linkGenerator,
    SatoshiTicketsRaffleBundleService raffleBundleService,
    ILogger<GreenfieldSatoshiTicketsController> logger) : ControllerBase
{

    [HttpGet("tickets")]
    public async Task<IActionResult> GetTickets(string storeId, string eventId, [FromQuery] string searchText = null)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();
        var eventExists = ctx.Events.Any(c => c.Id == eventId && c.StoreId == storeId);
        if (!eventExists)
            return EventNotFound();

        var query = ctx.Tickets.AsNoTracking().Where(t => t.EventId == eventId && t.StoreId == storeId && t.PaymentStatus == TransactionStatus.Settled.ToString());
        if (!string.IsNullOrEmpty(searchText))
        {
            searchText = searchText.Trim();
            query = query.Where(t =>
                t.TxnNumber.Contains(searchText) || t.FirstName.Contains(searchText) ||
                t.LastName.Contains(searchText) || t.Email.Contains(searchText) ||t.TicketNumber.Contains(searchText));
        }
        var tickets = query.ToList();
        var result = tickets.Select(ToTicketData).ToArray();
        return Ok(result);
    }


    /*[HttpPost("tickets/{ticketNumber}/check-in")]
    public async Task<IActionResult> CheckinTicket(string storeId, string eventId, string ticketNumber)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var ticketExist = ctx.Events.Any(c => c.Id == eventId && c.StoreId == storeId);
        if (!ticketExist)
            return EventNotFound();

        var checkinResult = await ticketService.CheckinTicket(eventId, ticketNumber, storeId);
        if (!checkinResult.Success)
            return this.CreateAPIError(422, "checkin-failed", checkinResult.ErrorMessage);

        var result = new CheckinResultData
        {
            Success = checkinResult.Success,
            ErrorMessage = checkinResult.ErrorMessage,
            Ticket = checkinResult.Ticket != null ? ToTicketData(checkinResult.Ticket) : null
        };
        return Ok(result);
    }*/


    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(string storeId, string eventId, [FromQuery] string searchText = null)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();
        var ticketExist = ctx.Events.Any(c => c.Id == eventId && c.StoreId == storeId);
        if (!ticketExist)
            return EventNotFound();

        var query = ctx.Orders.AsNoTracking().Include(c => c.Tickets)
            .Where(c => c.EventId == eventId && c.StoreId == storeId && c.PaymentStatus == TransactionStatus.Settled.ToString());

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(o =>
                o.InvoiceId.Contains(searchText) ||
                o.Tickets.Any(t =>
                    t.TxnNumber.Contains(searchText) ||
                    t.FirstName.Contains(searchText) ||
                    t.LastName.Contains(searchText) ||
                    t.Email.Contains(searchText)));
        }

        var orders = query.ToList();
        var result = orders.Select(ToOrderData).ToArray();
        return Ok(result);
    }

    [HttpPost("purchase")]
    public async Task<IActionResult> CreatePurchase(string storeId, string eventId, [FromBody] CreatePurchaseRequest request)
    {
        if (request?.Tickets == null || request.Tickets.Length == 0)
            return this.CreateAPIError(422, "validation-error", "At least one ticket item is required");

        if (request.OrderTotal.HasValue && request.OrderTotal.Value <= 0)
            return this.CreateAPIError(422, "validation-error", "OrderTotal must be greater than 0 when specified");

        var store = HttpContext.GetStoreData();
        if (store == null || store.Id != storeId)
            return this.CreateAPIError(404, "store-not-found", "The store was not found");

        await using var ctx = dbContextFactory.CreateContext();
        var ticketEvent = ctx.Events.FirstOrDefault(c => c.Id == eventId && c.StoreId == storeId);
        if (ticketEvent == null)
            return EventNotFound();

        var now = DateTime.UtcNow;
        if (ticketEvent.EventState == Data.EntityState.Disabled)
            return this.CreateAPIError(422, "event-not-active", "The event is not active");
        if (ticketEvent.StartDate.Date < now.Date)
            return this.CreateAPIError(422, "event-expired", "The event has already started or ended");
        if (ticketEvent.EndDate.HasValue && ticketEvent.EndDate.Value.Date < now.Date)
            return this.CreateAPIError(422, "event-expired", "The event has ended");

        var ticketTypes = ctx.TicketTypes.Where(t => t.EventId == eventId).ToDictionary(t => t.Id);
        var totalTicketsSold = ctx.Orders.AsNoTracking()
            .Where(c => c.StoreId == storeId && c.EventId == eventId && c.PaymentStatus == TransactionStatus.Settled.ToString())
            .SelectMany(c => c.Tickets).Count();

        if (GreenfieldPurchaseTicketValidator.Validate(this, ticketTypes, request.Tickets, ticketEvent, totalTicketsSold)
            is { } validationError)
            return validationError;

        var ticketsSum = request.Tickets.Sum(item => ticketTypes[item.TicketTypeId].Price * item.Quantity);
        if (request.OrderTotal.HasValue && request.OrderTotal.Value > 0 && request.OrderTotal.Value > ticketsSum)
            return this.CreateAPIError(422, "validation-error", "OrderTotal cannot exceed the sum of ticket prices");

        var txnId = Encoders.Base58.EncodeData(RandomUtils.GetBytes(10));
        var orderNow = DateTimeOffset.UtcNow;
        var order = new Order
        {
            TxnId = txnId,
            EventId = eventId,
            StoreId = storeId,
            Currency = ticketEvent.Currency,
            PaymentStatus = TransactionStatus.New.ToString(),
            CreatedAt = orderNow,
            TotalAmount = request.OrderTotal is > 0 ? request.OrderTotal.Value : ticketsSum
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        var tickets = new List<Ticket>();
        foreach (var item in request.Tickets)
        {
            var ticketType = ticketTypes[item.TicketTypeId];
            for (var i = 0; i < item.Quantity; i++)
            {
                var recipient = item.Recipients[i];
                var ticketTxn = Encoders.Base58.EncodeData(RandomUtils.GetBytes(10));
                var qrCodeLink = Url.Action("EventTicketDisplay", "UITicketSalesPublic",
                    new { storeId, eventId, orderId = order.Id, txnNumber = ticketTxn },
                    Request.Scheme, Request.Host.Value);
                var ticket = new Ticket
                {
                    StoreId = storeId,
                    EventId = eventId,
                    TicketTypeId = ticketType.Id,
                    Amount = ticketType.Price,
                    QRCodeLink = qrCodeLink,
                    FirstName = recipient.FirstName?.Trim() ?? string.Empty,
                    LastName = recipient.LastName?.Trim() ?? string.Empty,
                    Email = recipient.Email?.Trim() ?? string.Empty,
                    CreatedAt = orderNow,
                    TxnNumber = ticketTxn,
                    TicketNumber = $"EVT-{eventId}-{orderNow:yyMMdd}-{ticketTxn}",
                    TicketTypeName = ticketType.Name,
                    PaymentStatus = TransactionStatus.New.ToString()
                };
                tickets.Add(ticket);
            }
        }
        order.Tickets = tickets;
        ctx.Orders.Update(order);
        await ctx.SaveChangesAsync();

        var redirectUrl = !string.IsNullOrEmpty(request.RedirectUrl)
            ? request.RedirectUrl
            : ticketEvent.RedirectUrl ?? string.Empty;
        var invoice = await CreateInvoiceForOrder(store, order, ticketEvent.Currency, redirectUrl);

        order.InvoiceId = invoice.Id;
        order.InvoiceStatus = invoice.Status.ToString();
        ctx.Orders.Update(order);
        await ctx.SaveChangesAsync();

        var checkoutUrl = linkGenerator.InvoiceCheckoutLink(invoice.Id, Request.GetRequestBaseUrl());
        return StatusCode(201, new PurchaseResponse
        {
            OrderId = order.Id,
            TxnId = order.TxnId,
            InvoiceId = invoice.Id,
            CheckoutUrl = checkoutUrl
        });
    }

    [HttpPost("create-tickets-offline")]
    public async Task<IActionResult> CreateTicketsOffline(string storeId, string eventId, [FromBody] CreateTicketsOfflineRequest request)
    {
        if (request?.Tickets == null || request.Tickets.Length == 0)
            return this.CreateAPIError(422, "validation-error", "At least one ticket item is required");

        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();
        var ticketEvent = ctx.Events.FirstOrDefault(c => c.Id == eventId && c.StoreId == storeId);
        if (ticketEvent == null)
            return EventNotFound();

        var now = DateTime.UtcNow;
        if (ticketEvent.EventState == Data.EntityState.Disabled)
            return this.CreateAPIError(422, "event-not-active", "The event is not active");
        if (ticketEvent.StartDate.Date < now.Date)
            return this.CreateAPIError(422, "event-expired", "The event has already started or ended");
        if (ticketEvent.EndDate.HasValue && ticketEvent.EndDate.Value.Date < now.Date)
            return this.CreateAPIError(422, "event-expired", "The event has ended");

        var ticketTypes = ctx.TicketTypes.Where(t => t.EventId == eventId).ToDictionary(t => t.Id);
        var totalTicketsSold = ctx.Orders.AsNoTracking()
            .Where(c => c.StoreId == storeId && c.EventId == eventId && c.PaymentStatus == TransactionStatus.Settled.ToString())
            .SelectMany(c => c.Tickets).Count();

        if (GreenfieldPurchaseTicketValidator.Validate(this, ticketTypes, request.Tickets, ticketEvent, totalTicketsSold)
            is { } validationError)
            return validationError;

        var txnId = Encoders.Base58.EncodeData(RandomUtils.GetBytes(10));
        var orderNow = DateTimeOffset.UtcNow;
        var totalAmount = 0m;
        foreach (var item in request.Tickets)
        {
            var ticketType = ticketTypes[item.TicketTypeId];
            totalAmount += ticketType.Price * item.Quantity;
        }

        var order = new Order
        {
            TxnId = txnId,
            EventId = eventId,
            StoreId = storeId,
            Currency = ticketEvent.Currency,
            PaymentStatus = TransactionStatus.Settled.ToString(),
            InvoiceStatus = "n/a",
            CreatedAt = orderNow,
            PurchaseDate = orderNow,
            TotalAmount = totalAmount
        };
        ctx.Orders.Add(order);
        await ctx.SaveChangesAsync();

        var tickets = new List<Ticket>();
        foreach (var item in request.Tickets)
        {
            var ticketType = ticketTypes[item.TicketTypeId];
            for (var i = 0; i < item.Quantity; i++)
            {
                var recipient = item.Recipients[i];
                var ticketTxn = Encoders.Base58.EncodeData(RandomUtils.GetBytes(10));
                var qrCodeLink = Url.Action("EventTicketDisplay", "UITicketSalesPublic",
                    new { storeId, eventId, orderId = order.Id, txnNumber = ticketTxn },
                    Request.Scheme, Request.Host.Value);
                var ticket = new Ticket
                {
                    StoreId = storeId,
                    EventId = eventId,
                    TicketTypeId = ticketType.Id,
                    Amount = ticketType.Price,
                    QRCodeLink = qrCodeLink,
                    FirstName = recipient.FirstName?.Trim() ?? string.Empty,
                    LastName = recipient.LastName?.Trim() ?? string.Empty,
                    Email = recipient.Email?.Trim() ?? string.Empty,
                    CreatedAt = orderNow,
                    TxnNumber = ticketTxn,
                    TicketNumber = $"EVT-{eventId}-{orderNow:yyMMdd}-{ticketTxn}",
                    TicketTypeName = ticketType.Name,
                    PaymentStatus = TransactionStatus.Settled.ToString()
                };
                tickets.Add(ticket);
            }
        }
        order.Tickets = tickets;
        ctx.Orders.Update(order);
        await ctx.SaveChangesAsync();

        var ticketTypesList = ctx.TicketTypes.Where(t => t.EventId == eventId).ToList();
        var ticketCounts = tickets.GroupBy(t => t.TicketTypeId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var ticketType in ticketTypesList)
        {
            if (ticketCounts.TryGetValue(ticketType.Id, out var count))
            {
                ticketType.QuantitySold += count;
            }
        }
        ctx.TicketTypes.UpdateRange(ticketTypesList);
        await ctx.SaveChangesAsync();

        var emailSent = false;
        var sender = await emailSenderFactory.GetEmailSender(storeId);
        var settings = await sender.GetEmailSettings();
        if (settings?.IsComplete() == true)
        {
            try
            {
                await emailService.SendTicketRegistrationEmail(storeId, tickets, ticketEvent);
                order.EmailSent = true;
                emailSent = true;
                ctx.Orders.Update(order);
                await ctx.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send email for offline order {OrderId}", order.Id);
            }
        }

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        await raffleBundleService.AllocateForOrderAsync(storeId, order, ticketEvent, baseUrl);

        return Ok(new CreateTicketsOfflineResponse
        {
            OrderId = order.Id,
            TxnId = order.TxnId,
            OrderReference = request.OrderReference ?? string.Empty,
            TicketsCreated = tickets.Count,
            EmailSent = emailSent
        });
    }

    private async Task<BTCPayServer.Services.Invoices.InvoiceEntity> CreateInvoiceForOrder(
        BTCPayServer.Data.StoreData store, Order order, string currency, string redirectUrl)
    {
        var ticketSalesSearchTerm = $"{SimpleTicketSalesHostedService.TICKET_SALES_PREFIX}{order.TxnId}";
        var matchedExistingInvoices = await invoiceRepository.GetInvoices(new InvoiceQuery
        {
            TextSearch = ticketSalesSearchTerm,
            StoreId = new[] { store.Id }
        });
        matchedExistingInvoices = matchedExistingInvoices
            .Where(entity => entity.GetInternalTags(ticketSalesSearchTerm).Any(s => s == order.TxnId.ToString()))
            .ToArray();

        var settledInvoice = matchedExistingInvoices.LastOrDefault(entity =>
            new[] { "settled", "processing", "confirmed", "paid", "complete" }
                .Contains(entity.GetInvoiceState().Status.ToString().ToLower()));
        if (settledInvoice != null)
            return settledInvoice;

        var invoiceRequest = new BTCPayServer.Client.Models.CreateInvoiceRequest
        {
            Amount = order.TotalAmount,
            Currency = currency,
            Metadata = new JObject
            {
                ["orderId"] = order.Id,
                ["TxnId"] = order.TxnId
            },
            AdditionalSearchTerms = new[]
            {
                order.TxnId.ToString(CultureInfo.InvariantCulture),
                order.Id.ToString(CultureInfo.InvariantCulture),
                ticketSalesSearchTerm
            }
        };
        if (!string.IsNullOrEmpty(redirectUrl))
        {
            invoiceRequest.Checkout = new()
            {
                RedirectURL = redirectUrl
            };
        }
        return await invoiceController.CreateInvoiceCoreRaw(invoiceRequest, store,
            Request.GetAbsoluteRoot(), new List<string> { ticketSalesSearchTerm });
    }

    [HttpPost("orders/{orderId}/tickets/{ticketId}/send-reminder")]
    public async Task<IActionResult> SendReminder(string storeId, string eventId, string orderId, string ticketId)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();

        var ticketEvent = ctx.Events.FirstOrDefault(c => c.Id == eventId && c.StoreId == storeId);
        if (ticketEvent == null)
            return EventNotFound();

        var order = ctx.Orders.AsNoTracking().Include(c => c.Tickets)
            .FirstOrDefault(o => o.Id == orderId && o.StoreId == storeId && o.EventId == eventId && o.Tickets.Any());
        if (order == null)
            return this.CreateAPIError(404, "order-not-found", "The order was not found");

        var ticket = order.Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (ticket == null)
            return this.CreateAPIError(404, "ticket-not-found", "The ticket was not found");

        var isEmailConfigured = await emailService.IsEmailSettingsConfigured(storeId);
        if (!isEmailConfigured)
        {
            return this.CreateAPIError(422, "email-not-configured",
                "Email SMTP settings are not configured. Configure email settings in the store admin.");
        }
        try
        {
            var emailResponse = await emailService.SendTicketRegistrationEmail(storeId, ticket, ticketEvent);
            if (emailResponse.IsSuccessful)
            {
                order.EmailSent = true;
                ctx.Orders.Update(order);
                await ctx.SaveChangesAsync();
            }
            else
            {
                var failedList = emailResponse.FailedRecipients?.Count > 0
                    ? string.Join(", ", emailResponse.FailedRecipients) : ticket.Email;
                return this.CreateAPIError(500, "email-send-failed", $"Failed to send ticket email to: {failedList}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send ticket reminder (store={StoreId}, event={EventId}, order={OrderId})",
                storeId, eventId, orderId);
            return this.CreateAPIError(500, "email-send-failed",
                "An internal error occurred while sending ticket details");
        }
        return Ok(new { success = true, message = "Ticket details have been sent to the recipient via email" });
    }

    private static TicketData ToTicketData(Ticket entity)
    {
        return new TicketData
        {
            Id = entity.Id,
            EventId = entity.EventId,
            TicketTypeId = entity.TicketTypeId,
            TicketTypeName = entity.TicketTypeName,
            Amount = entity.Amount,
            FirstName = entity.FirstName,
            LastName = entity.LastName,
            Email = entity.Email,
            TicketNumber = entity.TicketNumber,
            TxnNumber = entity.TxnNumber,
            PaymentStatus = entity.PaymentStatus,
            CheckedIn = entity.UsedAt.HasValue,
            CheckedInAt = entity.UsedAt,
            EmailSent = entity.EmailSent,
            CreatedAt = entity.CreatedAt
        };
    }

    private static OrderData ToOrderData(Order entity)
    {
        return new OrderData
        {
            Id = entity.Id,
            EventId = entity.EventId,
            TotalAmount = entity.TotalAmount,
            Currency = entity.Currency,
            InvoiceId = entity.InvoiceId,
            PaymentStatus = entity.PaymentStatus,
            InvoiceStatus = entity.InvoiceStatus,
            EmailSent = entity.EmailSent,
            CreatedAt = entity.CreatedAt,
            PurchaseDate = entity.PurchaseDate,
            Tickets = entity.Tickets?.Select(ToTicketData).ToList() ?? new()
        };
    }

    private IActionResult EventNotFound()
    {
        return this.CreateAPIError(404, "event-not-found", "The event was not found");
    }
}
