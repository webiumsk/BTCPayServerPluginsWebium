using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Models;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Helper;
using BTCPayServer.Plugins.SatfluxTickets.Helper.Extensions;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Plugins.SatfluxTickets.ViewModels;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Rates;
using BTCPayServer.Rating;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using TransactionStatus = BTCPayServer.Plugins.SatfluxTickets.Data.TransactionStatus;

namespace BTCPayServer.Plugins.SatfluxTickets;

[AllowAnonymous]
[Route("~/plugins/{storeId}/satflux-tickets/public/", Order = 1)]
[Route("~/plugins/{storeId}/satoshi-tickets/public/", Order = 3)] // legacy alias - ticket links already delivered by email/QR
[Route("~/plugins/{storeId}/ticket/public/", Order = 2)]
public class UITicketSalesPublicController(UriResolver uriResolver,
        IFileService fileService,
        StoreRepository storeRepo,
        EmailService emailService,
        TicketService ticketService,
        InvoiceRepository invoiceRepository,
        UIInvoiceController invoiceController,
        SimpleTicketSalesDbContextFactory dbContextFactory,
        CurrencyNameTable currencyTable,
        ILogger<UITicketSalesPublicController> logger,
        SimpleTicketSalesHostedService salesHostedService) : Controller
{
    private const string SessionKeyOrder = "Ticket_Order_";

    // Existing public and emailed URLs remain valid. Quantities and recipients now share one page.
    [HttpGet("event/{eventId}/summary")]
    public Task<IActionResult> EventSummary(string storeId, string eventId, string txnId = null) => SalesPage(storeId, eventId, txnId);

    [HttpGet("event/{eventId}/summary/tickets")]
    public Task<IActionResult> EventTicket(string storeId, string eventId, string txnId = null) => SalesPage(storeId, eventId, txnId);

    [HttpGet("event/{eventId}/summary/contact")]
    public Task<IActionResult> EventContactDetails(string storeId, string eventId, string txnId) => SalesPage(storeId, eventId, txnId, true);

    // Keep older URLs compatible, but make every page reference content-addressed asset paths.
    [HttpGet("sales-assets/ticket-sales.js")]
    public IActionResult SalesScript() => SalesAsset(false, null);

    [HttpGet("sales-assets/ticket-sales.css")]
    public IActionResult SalesStyle() => SalesAsset(true, null);

    [HttpGet("sales-assets/{version}/ticket-sales.js")]
    public IActionResult SalesScriptVersioned(string version) => SalesAsset(false, version, true);

    [HttpGet("sales-assets/{version}/ticket-sales.css")]
    public IActionResult SalesStyleVersioned(string version) => SalesAsset(true, version, true);

    private IActionResult SalesAsset(bool style, string version, bool versioned = false)
    {
        var expected = style ? TicketSalesText.StyleVersion : TicketSalesText.ScriptVersion;
        Response.Headers.CacheControl = "no-store";
        if (versioned && !string.Equals(version, expected, StringComparison.Ordinal)) return NotFound();
        // Revalidate even a versioned path: a mismatched worker must never serve another asset's bytes.
        Response.Headers.CacheControl = versioned ? "no-cache, max-age=0, must-revalidate" : "no-store";
        Response.Headers.ETag = "\"" + expected + "\"";
        return Content(emailService.GetEmbeddedResourceContent(style ? "Resources.css.ticket-sales.css" : "Resources.js.ticket-sales.js"),
            style ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8");
    }

    private static string OrderSessionKey(string eventId, string txnId) => $"{SessionKeyOrder}{eventId}_{txnId}";

    private TicketOrderViewModel SessionOrder(string storeId, string eventId, string txnId)
    {
        if (string.IsNullOrEmpty(txnId) || txnId.Length > 32) return null;
        var order = HttpContext.Session.GetObject<TicketOrderViewModel>(OrderSessionKey(eventId, txnId));
        return order?.StoreId == storeId && order.EventId == eventId && order.TxnId == txnId ? order : null;
    }

    private async Task<IActionResult> SalesPage(string storeId, string eventId, string txnId, bool contactsOpen = false,
        TicketSalesSubmission draft = null, string errorKey = null, bool allowNewSession = true)
    {
        // This shared public URL can contain a session-specific recipient draft after correction/recovery.
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
        var store = await storeRepo.FindStore(storeId);
        if (store == null) return NotFound();
        await using var ctx = dbContextFactory.CreateContext();
        var ev = await ctx.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.StoreId == storeId);
        if (ev == null) return NotFound();
        var activeKey = $"{SessionKeyOrder}{storeId}_{eventId}_Active";
        if (string.IsNullOrEmpty(txnId)) txnId = HttpContext.Session.GetString(activeKey);
        var sessionOrder = SessionOrder(storeId, eventId, txnId);
        if (sessionOrder == null)
        {
            if (!allowNewSession) return BadRequest();
            if (!string.IsNullOrEmpty(txnId)) errorKey = "SessionExpired";
            txnId = Encoders.Base58.EncodeData(RandomUtils.GetBytes(10));
            sessionOrder = new TicketOrderViewModel { TxnId = txnId, StoreId = storeId, EventId = eventId };
            HttpContext.Session.SetObject(OrderSessionKey(eventId, txnId), sessionOrder);
            HttpContext.Session.SetString(activeKey, txnId);
            if (draft != null) draft.TxnId = txnId;
        }
        var sessionKey = OrderSessionKey(eventId, txnId);
        draft ??= HttpContext.Session.GetObject<TicketSalesSubmission>(sessionKey + "_SalesDraft");
        draft ??= new TicketSalesSubmission
        {
            TxnId = txnId, Tickets = sessionOrder.Tickets,
            ContactInfo = TicketSalesValidation.MapLegacyContacts(sessionOrder),
            SharedEmail = sessionOrder.ContactInfo?.FirstOrDefault()?.Email
        };
        var existing = await ctx.Orders.AsNoTracking().Include(o => o.Tickets)
            .FirstOrDefaultAsync(o => o.TxnId == txnId && o.EventId == eventId && o.StoreId == storeId);
        if (existing == null && !ValidateEvent(ctx, storeId, eventId, checkCapacity: false)) return NotFound();
        if (existing != null)
        {
            // Once an external invoice may exist, its original intent and recipients stay immutable.
            draft.Tickets = existing.Tickets.GroupBy(t => t.TicketTypeId).Select(g => new TicketSelectionViewModel
            {
                TicketTypeId = g.Key, TicketTypeName = g.First().TicketTypeName, Price = g.First().Amount, Quantity = g.Count()
            }).ToList();
            var groupedTickets = existing.Tickets.GroupBy(ticket => ticket.TicketTypeId).ToList();
            draft.ContactInfo = groupedTickets.SelectMany(group => group.OrderBy(ticket => ticket.Id).Select((ticket, index) => new TicketSalesContactViewModel
            {
                TicketTypeId = group.Key, AdmissionIndex = index,
                FirstName = ticket.FirstName, LastName = ticket.LastName, Email = ticket.Email
            })).ToList();
            draft.SharedEmail = draft.ContactInfo.FirstOrDefault()?.Email;
            draft.DeliveryOption = draft.ContactInfo.Select(c => c.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? "together" : "individual";
        }
        var text = new TicketSalesText(TicketSalesText.ResolveLanguage(Request.Query.ContainsKey("lang") ? Request.Query["lang"].ToString() : draft.Language,
            Request.Headers.AcceptLanguage.ToString()));
        var image = ev.EventLogo == null ? null : await fileService.GetFileUrl(Request.GetAbsoluteRootUri(), ev.EventLogo);
        var settledCount = await SettledCount(ctx, storeId, eventId);
        var eventRemaining = ev.HasMaximumCapacity
            ? Math.Max(0, (ev.MaximumEventCapacity ?? 0) - settledCount) : TicketSalesValidation.MaximumAdmissions;
        var types = await ctx.TicketTypes.AsNoTracking().Where(t => t.EventId == eventId && t.TicketTypeState == Data.EntityState.Active)
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Id).ToListAsync();
        var vm = new TicketSalesPageViewModel
        {
            StoreId = storeId, StoreName = store.StoreName,
            StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, store.GetStoreBlob()),
            EventId = ev.Id, EventTitle = ev.Title, Description = ev.Description,
            EventImageUrl = image == null ? null : await uriResolver.Resolve(Request.GetAbsoluteRootUri(), new UnresolvedUri.Raw(image)),
            EventType = ev.EventType, Location = ev.EventType == Data.EventType.Physical ? ev.Location : null, StartDate = ev.StartDate, EndDate = ev.EndDate,
            Currency = ev.Currency, CurrencyDecimals = currencyTable.GetNumberFormatInfo(ev.Currency, false)?.CurrencyDecimalDigits
                ?? currencyTable.GetCurrencyData(ev.Currency, true)?.Divisibility ?? 2,
            TxnId = txnId, ContactInfo = draft.ContactInfo ?? new(), DeliveryOption = draft.DeliveryOption,
            SharedEmail = draft.SharedEmail, ErrorKey = errorKey, ContactsOpen = contactsOpen,
            CheckoutStarted = existing != null,
            CheckoutUrl = existing?.InvoiceId == null ? null : Url.Action(nameof(UIInvoiceController.Checkout), "UIInvoice", new { invoiceId = existing.InvoiceId }),
            Text = text,
            IsPhysical = ev.EventType == EventType.Physical,
            MissingVenueTimeZone = ev.EventType == EventType.Physical && !EventTimeZoneService.TryGetStored(ev.TimeZone, out _),
            VenueDates = ev.EventType == EventType.Physical && EventTimeZoneService.TryGetStored(ev.TimeZone, out _)
                ? TicketSalesText.Translations().Keys.ToDictionary(language => language, language => new TicketSalesVenueDates
                {
                    Start = EventTimeZoneService.VenueDate(ev.StartDate, ev.TimeZone, new TicketSalesText(language).Culture),
                    End = ev.EndDate.HasValue ? EventTimeZoneService.VenueDate(ev.EndDate.Value, ev.TimeZone, new TicketSalesText(language).Culture) : null,
                    Label = new TicketSalesText(language)["VenueTimezone"] + ": " + EventTimeZoneService.StoredId(ev.TimeZone)
                }) : new(),
            TicketTypes = types.Select(t => new TicketSalesTypeViewModel
            {
                Id = t.Id, Name = t.Name, Description = t.Description, Price = t.Price,
                Maximum = Math.Min(eventRemaining, Math.Min(TicketSalesValidation.MaximumPerType,
                    (ev.HasMaximumCapacity || t.Quantity > 0) ? Math.Max(0, t.Quantity - t.QuantitySold) : TicketSalesValidation.MaximumPerType)),
                // Do not silently clamp a failed selection; the user needs to see and correct it.
                Selected = draft.Tickets?.FirstOrDefault(r => r.TicketTypeId == t.Id)?.Quantity ?? 0
            }).ToList()
        };
        if (existing != null)
        {
            // Keep an invoice-in-progress visible even when a type has since been disabled or reordered.
            vm.TicketTypes = draft.Tickets.Select(t => new TicketSalesTypeViewModel
            {
                Id = t.TicketTypeId, Name = t.TicketTypeName, Price = t.Price, Selected = t.Quantity, Maximum = t.Quantity
            }).ToList();
        }
        ViewData["TicketSalesLanguage"] = text.Language;
        ViewData["Title"] = ev.Title;
        return View("EventSummary", vm);
    }

    private static Task<int> SettledCount(SimpleTicketSalesDbContext ctx, string storeId, string eventId) =>
        ctx.Orders.AsNoTracking().Where(o => o.StoreId == storeId && o.EventId == eventId
                && o.PaymentStatus == TransactionStatus.Settled.ToString()).SelectMany(o => o.Tickets).CountAsync();

    // Presentation only: preserve incomplete quantities/contacts without creating an order or invoice.
    [HttpPost("event/{eventId}/summary/language")]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 4096)]
    [RequestSizeLimit(131072)]
    public Task<IActionResult> ChangeLanguage(string storeId, string eventId, TicketSalesSubmission model)
    {
        if (!TicketSalesValidation.HasBoundedShape(model) || (model.Language?.Length ?? 0) > 35
            || SessionOrder(storeId, eventId, model.TxnId) == null)
            return Task.FromResult<IActionResult>(BadRequest());
        return SalesPage(storeId, eventId, model.TxnId, model.ContactsOpen, model, allowNewSession: false);
    }

    [HttpPost("event/{eventId}/summary")]
    [RequestFormLimits(ValueCountLimit = 4096)]
    [RequestSizeLimit(131072)]
    public async Task<IActionResult> SubmitSale(string storeId, string eventId, TicketSalesSubmission model)
    {
        if (!TicketSalesValidation.HasBoundedShape(model) || (model.Language?.Length ?? 0) > 35) return BadRequest();
        var sessionOrder = SessionOrder(storeId, eventId, model.TxnId);
        if (sessionOrder == null)
            return await SalesPage(storeId, eventId, null, true, model, "SessionExpired");
        var store = await storeRepo.FindStore(storeId);
        if (store == null) return NotFound();
        await using var ctx = dbContextFactory.CreateContext(options =>
            options.ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies)));
        await using var checkoutLock = await TicketSalesCheckoutLock.AcquireAsync(ctx, storeId, eventId, model.TxnId, HttpContext.RequestAborted);
        var existing = await ctx.Orders.Include(o => o.Tickets).FirstOrDefaultAsync(o => o.StoreId == storeId && o.EventId == eventId && o.TxnId == model.TxnId);
        if (!string.IsNullOrEmpty(existing?.InvoiceId))
        {
            salesHostedService.QueuePublicInvoiceReconciliation(existing.InvoiceId);
            return RedirectToAction(nameof(UIInvoiceController.Checkout), "UIInvoice", new { invoiceId = existing.InvoiceId });
        }
        var ev = await ctx.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.StoreId == storeId);
        // An immutable attempted intent is lookup-only and remains recoverable if the event later closes.
        if ((existing == null || existing.InvoiceStatus != TicketSalesInvoiceRecovery.CreationAttempted)
            && (ev == null || !ValidateEvent(ctx, storeId, eventId))) return NotFound();
        var sessionKey = OrderSessionKey(eventId, model.TxnId);
        // Lookup-only recovery of an attempted checkout must survive a later settings change.
        if (existing?.InvoiceStatus != TicketSalesInvoiceRecovery.CreationAttempted
            && !TicketRedirectUrlAttribute.IsAllowed(ev?.RedirectUrl))
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return await SalesPage(storeId, eventId, model.TxnId, true, model, "CheckoutConfigurationInvalid", false);
        }
        if (existing == null)
        {
            // Bind errors and malformed indices are never accepted, even when the visible portion looks valid.
            var types = await ctx.TicketTypes.AsNoTracking().Where(t => t.EventId == eventId).ToListAsync();
            var error = TicketSalesValidation.ValidateSelection(ev, types, model.Tickets, await SettledCount(ctx, storeId, eventId), out var selected);
            List<TicketContactInfoViewModel> contacts = null;
            error ??= TicketSalesValidation.ValidateRecipients(selected, model.ContactInfo, model.DeliveryOption, model.SharedEmail, out contacts);
            if (!ModelState.IsValid) error = "ContactsInvalid";
            HttpContext.Session.SetObject(sessionKey + "_SalesDraft", model);
            if (error != null)
            {
                Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return await SalesPage(storeId, eventId, model.TxnId, true, model, error, false);
            }
            sessionOrder.Tickets = selected;
            sessionOrder.ContactInfo = contacts;
            sessionOrder.IsStepOneComplete = sessionOrder.IsStepTwoComplete = true;
            HttpContext.Session.SetObject(sessionKey, sessionOrder);
            var now = DateTimeOffset.UtcNow;
            existing = new Order
            {
                Id = Guid.NewGuid().ToString(), TxnId = model.TxnId, StoreId = storeId, EventId = eventId, Currency = ev.Currency.Trim().ToUpperInvariant(),
                PaymentStatus = TransactionStatus.New.ToString(), CreatedAt = now
            };
            foreach (var contact in contacts)
            {
                var type = selected.Single(t => t.TicketTypeId == contact.TicketTypeId);
                var ticketTxn = Encoders.Base58.EncodeData(RandomUtils.GetBytes(10));
                existing.Tickets.Add(new Ticket
                {
                    StoreId = storeId, EventId = eventId, TicketTypeId = type.TicketTypeId, TicketTypeName = type.TicketTypeName,
                    Amount = type.Price, FirstName = contact.FirstName, LastName = contact.LastName, Email = contact.Email,
                    QRCodeLink = Url.Action(nameof(EventTicketDisplay), "UITicketSalesPublic",
                        new { storeId, eventId, orderId = existing.Id, txnNumber = ticketTxn }, Request.Scheme),
                    CreatedAt = now, TxnNumber = ticketTxn, TicketNumber = $"EVT-{eventId}-{now:yyMMdd}-{ticketTxn}",
                    PaymentStatus = TransactionStatus.New.ToString()
                });
            }
            var rawTotal = existing.Tickets.Sum(t => t.Amount);
            var currencyFormat = currencyTable.GetNumberFormatInfo(existing.Currency, false);
            existing.TotalAmount = currencyFormat == null ? rawTotal : rawTotal.RoundToSignificant(currencyFormat.CurrencyDecimalDigits);
            ctx.Orders.Add(existing);
            // Persist reconciliation evidence before making an external request that could succeed despite a timeout.
            await ctx.SaveChangesAsync();
        }
        try
        {
            var invoice = await CreateInvoice(store, existing, existing.Currency, Request.GetAbsoluteRoot(), ev?.RedirectUrl ?? string.Empty,
                async () => { await ctx.SaveChangesAsync(); });
            existing.InvoiceId = invoice.Id;
            existing.InvoiceStatus = invoice.Status.ToString();
            await ctx.SaveChangesAsync();
            salesHostedService.QueuePublicInvoiceReconciliation(invoice.Id);
            HttpContext.Session.Remove($"{SessionKeyOrder}{storeId}_{eventId}_Active");
            return RedirectToAction(nameof(UIInvoiceController.Checkout), "UIInvoice", new { invoiceId = invoice.Id });
        }
        catch (TicketSalesInvoicePendingException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return await SalesPage(storeId, eventId, model.TxnId, true, model, "PendingVerification", false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not log recipient details, raw upstream response bodies or credentials.
            logger.LogWarning("Public ticket invoice opening failed ({ExceptionType}); retained checkout intent for retry", ex.GetType().Name);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return await SalesPage(storeId, eventId, model.TxnId, true, model, "UpstreamError", false);
        }
    }

    // Keep submissions from a page opened before the upgrade working, under the same new validation.
    [HttpPost("save-event-tickets")]
    public async Task<IActionResult> SaveEventTickets(string storeId, string eventId, EventTicketPageViewModel model)
    {
        if (model?.Tickets == null || model.Tickets.Count > TicketSalesValidation.MaximumTypes) return BadRequest();
        await using var ctx = dbContextFactory.CreateContext();
        var ev = await ctx.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.StoreId == storeId);
        if (ev == null || !ValidateEvent(ctx, storeId, eventId)) return NotFound();
        var types = await ctx.TicketTypes.AsNoTracking().Where(t => t.EventId == eventId).ToListAsync();
        var error = TicketSalesValidation.ValidateSelection(ev, types, model.Tickets, await SettledCount(ctx, storeId, eventId), out var canonical);
        var txnId = Encoders.Base58.EncodeData(RandomUtils.GetBytes(10));
        HttpContext.Session.SetObject(OrderSessionKey(eventId, txnId), new TicketOrderViewModel
        {
            TxnId = txnId, EventId = eventId, StoreId = storeId, Tickets = error == null ? canonical : new(), IsStepOneComplete = error == null
        });
        return await SalesPage(storeId, eventId, txnId, error == null, error == null ? null : new TicketSalesSubmission { TxnId = txnId, Tickets = model.Tickets }, error);
    }

    [HttpPost("save-contact-details")]
    public async Task<IActionResult> SaveContactDetails(string storeId, string eventId, ContactInfoPageViewModel model)
    {
        var order = SessionOrder(storeId, eventId, model?.TxnId);
        if (order == null) return await SalesPage(storeId, eventId, null, false, errorKey: "SessionExpired");
        var individual = Request.Form["ticketDeliveryOption"] == "individual";
        var contacts = new List<TicketSalesContactViewModel>();
        var index = 0;
        foreach (var item in order.Tickets)
        for (var i = 0; i < item.Quantity; i++)
        {
            var contact = model.ContactInfo?.ElementAtOrDefault(individual ? index : 0);
            contacts.Add(new TicketSalesContactViewModel
            {
                TicketTypeId = item.TicketTypeId, AdmissionIndex = i,
                FirstName = contact?.FirstName, LastName = contact?.LastName, Email = contact?.Email
            });
            index++;
        }
        return await SubmitSale(storeId, eventId, new TicketSalesSubmission
        {
            TxnId = model.TxnId, Tickets = order.Tickets, ContactInfo = contacts,
            DeliveryOption = individual ? "individual" : "together", SharedEmail = contacts.FirstOrDefault()?.Email
        });
    }

    [HttpGet("event/{eventId}/ticket/{orderId}/summary")]
    public async Task<IActionResult> EventTicketDisplay(string storeId, string eventId, string orderId, string txnNumber)
    {
        var store = await storeRepo.FindStore(storeId);
        if (store == null) return NotFound();

        await using var ctx = dbContextFactory.CreateContext();

        var order = ctx.Orders.AsNoTracking().Include(c => c.Tickets).FirstOrDefault(o => o.StoreId == storeId && o.EventId == eventId && o.Id == orderId);
        if (order?.Tickets?.Any() != true) return NotFound();

        var tickets = order.Tickets.AsEnumerable();
        if (!string.IsNullOrEmpty(txnNumber))
        {
            if (order.Tickets.FirstOrDefault(c => c.TxnNumber == txnNumber) == null) return NotFound();
            tickets = order.Tickets.Where(c => c.TxnNumber == txnNumber);
        }
        var ticketEvent = ctx.Events.FirstOrDefault(c => c.StoreId == storeId && c.Id == eventId);
        if (ticketEvent == null || !tickets.Any()) return NotFound();

        return View(new TicketViewModel
        {
            EventName = ticketEvent.Title,
            Location = ticketEvent.Location,
            StartDate = ticketEvent.StartDate,
            DisplayStart = ticketEvent.EventType == EventType.Physical && EventTimeZoneService.TryGetStored(ticketEvent.TimeZone, out _)
                ? EventTimeZoneService.VenueDate(ticketEvent.StartDate, ticketEvent.TimeZone, new TicketSalesText(TicketSalesText.ResolveLanguage(Request.Query["lang"], Request.Headers.AcceptLanguage)).Culture) : null,
            DisplayEnd = ticketEvent.EventType == EventType.Physical && ticketEvent.EndDate.HasValue && EventTimeZoneService.TryGetStored(ticketEvent.TimeZone, out _)
                ? EventTimeZoneService.VenueDate(ticketEvent.EndDate.Value, ticketEvent.TimeZone, new TicketSalesText(TicketSalesText.ResolveLanguage(Request.Query["lang"], Request.Headers.AcceptLanguage)).Culture) : null,
            TimeZoneLabel = ticketEvent.EventType == EventType.Physical
                ? new TicketSalesText(TicketSalesText.ResolveLanguage(Request.Query["lang"], Request.Headers.AcceptLanguage))[EventTimeZoneService.TryGetStored(ticketEvent.TimeZone, out _) ? "VenueTimezone" : "MissingVenueTimezone"]
                    + (EventTimeZoneService.TryGetStored(ticketEvent.TimeZone, out _) ? ": " + EventTimeZoneService.StoredId(ticketEvent.TimeZone) : "") : null,
            EndDate = ticketEvent.EndDate,
            PurchaseDate = order.PurchaseDate ?? order.CreatedAt,
            Tickets = tickets.Select(t => new TicketListViewModel
            {
                FirstName = t.FirstName,
                LastName = t.LastName,
                Email = t.Email,
                Currency = order.Currency,
                Amount = t.Amount,
                TicketNumber = t.TicketNumber,
                TxnNumber = t.TxnNumber,
                TicketType = t.TicketTypeName,
                QrCodeUrl = TicketQrCode.CreateDataUrl(t.TicketNumber),
            }).ToList(),
            StoreBranding = await StoreBrandingViewModel.CreateAsync(Request, uriResolver, store.GetStoreBlob()),
        });
    }

    [HttpGet("satoshiticket/jsqr_min.js")]
    public async Task<IActionResult> GetQRScannerJs(string storeId)
    {
        var store = await storeRepo.FindStore(storeId);
        if (store == null) return NotFound();

        return Content(emailService.GetEmbeddedResourceContent("Resources.js.jsqr_min.js"), "text/javascript");
    }

    [HttpGet("{eventId}/check-in/{token}")]
    public async Task<IActionResult> TicketCheckin(string storeId, string eventId, string token)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var ev = ctx.Events.FirstOrDefault(e => e.Id == eventId && e.StoreId == storeId);
        var allSettings = await storeRepo.GetSettingAsync<Dictionary<string, EventCheckInSettings>>(storeId, Plugin.CheckinSettingsName);
        var settings = allSettings?.GetValueOrDefault(eventId);
        if (ev == null || !CheckInTokenHelper.VerifyToken(token, settings))
            return NotFound();

        var pinRequired = settings is { PinEnabled: true, } && HttpContext.Session.GetString($"CheckIn_{eventId}") != "authorized";
        return View(new TicketScannerViewModel
        {
            StoreId = storeId,
            EventId = eventId,
            EventName = ev.Title,
            Token = token,
            PinRequired = pinRequired
        });
    }

    [HttpPost("{eventId}/check-in/{token}/verify-pin")]
    public async Task<IActionResult> VerifyPin(string storeId, string eventId, string token, string pin)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var ev = ctx.Events.FirstOrDefault(e => e.Id == eventId && e.StoreId == storeId);
        var allSettings = await storeRepo.GetSettingAsync<Dictionary<string, EventCheckInSettings>>(storeId, Plugin.CheckinSettingsName);
        var settings = allSettings?.GetValueOrDefault(eventId);
        if (ev == null || !CheckInTokenHelper.VerifyToken(token, settings))
            return NotFound();

        if (settings == null || !settings.PinEnabled)
            return RedirectToAction(nameof(TicketCheckin), new { storeId, eventId, token });

        const int maxAttempts = 3;
        const int lockoutMinutes = 15;
        var attemptsKey = $"CheckIn_{eventId}_attempts";
        var lockoutKey = $"CheckIn_{eventId}_lockout";

        var lockoutStr = HttpContext.Session.GetString(lockoutKey);
        if (lockoutStr != null && DateTimeOffset.TryParse(lockoutStr, out var lockedUntil) && lockedUntil > DateTimeOffset.UtcNow)
        {
            TempData["CheckInErrorMessage"] = $"Too many incorrect attempts. Try again in {(int)(lockedUntil - DateTimeOffset.UtcNow).TotalMinutes + 1} minutes.";
            return RedirectToAction(nameof(TicketCheckin), new { storeId, eventId, token });
        }

        if (!CheckInTokenHelper.VerifyPin(pin, settings.PinHash))
        {
            var attempts = int.TryParse(HttpContext.Session.GetString(attemptsKey), out var a) ? a + 1 : 1;
            HttpContext.Session.SetString(attemptsKey, attempts.ToString());

            if (attempts >= maxAttempts)
            {
                var lockoutUntil = DateTimeOffset.UtcNow.AddMinutes(lockoutMinutes);
                HttpContext.Session.SetString(lockoutKey, lockoutUntil.ToString("o"));
                HttpContext.Session.Remove(attemptsKey);
                TempData["CheckInErrorMessage"] = $"Too many incorrect attempts. Locked out for {lockoutMinutes} minutes.";
            }
            else
            {
                TempData["CheckInErrorMessage"] = $"Invalid PIN. {maxAttempts - attempts} attempt(s) remaining.";
            }
            return RedirectToAction(nameof(TicketCheckin), new { storeId, eventId, token });
        }
        HttpContext.Session.Remove(attemptsKey);
        HttpContext.Session.Remove(lockoutKey);
        HttpContext.Session.SetString($"CheckIn_{eventId}", "authorized");
        return RedirectToAction(nameof(TicketCheckin), new { storeId, eventId, token });
    }


    [HttpPost("{eventId}/check-in/{token}/ticket")]
    public async Task<IActionResult> Checkin(string storeId, string eventId, string token, string ticketNumber)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var ev = ctx.Events.FirstOrDefault(e => e.Id == eventId && e.StoreId == storeId);
        var allSettings = await storeRepo.GetSettingAsync<Dictionary<string, EventCheckInSettings>>(storeId, Plugin.CheckinSettingsName);
        var settings = allSettings?.GetValueOrDefault(eventId);
        if (ev == null || !CheckInTokenHelper.VerifyToken(token, settings))
            return NotFound();

        if (settings is { PinEnabled: true } && HttpContext.Session.GetString($"CheckIn_{eventId}") != "authorized")
            return NotFound();

        var checkinTicket = await ticketService.CheckinTicket(eventId, ticketNumber, storeId);
        if (checkinTicket.Success)
        {
            TempData["CheckInSuccessMessage"] = $"Ticket for {checkinTicket.Ticket.FirstName} {checkinTicket.Ticket.LastName} of ticket type: {checkinTicket.Ticket.TicketTypeName} checked-in successfully";
        }
        else
        {
            TempData["CheckInErrorMessage"] = checkinTicket.ErrorMessage;
        }
        return RedirectToAction(nameof(TicketCheckin), new { storeId, eventId, token });
    }

    private async Task<InvoiceEntity> CreateInvoice(BTCPayServer.Data.StoreData store, Order order, string currency, string url, string redirectUrl, Func<Task> persistCreationAttempt)
    {
        var ticketSalesSearchTerm = $"{SimpleTicketSalesHostedService.TICKET_SALES_PREFIX}{order.TxnId}";
        var invoiceRequest = new CreateInvoiceRequest()
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
        return await TicketSalesInvoiceRecovery.FindOrCreateAsync(order,
            () => invoiceRepository.GetInvoices(new InvoiceQuery { TextSearch = ticketSalesSearchTerm, StoreId = new[] { store.Id } }),
            persistCreationAttempt,
            () => invoiceController.CreateInvoiceCoreRaw(invoiceRequest, store, url, new List<string> { ticketSalesSearchTerm }));
    }

    private bool ValidateEvent(SimpleTicketSalesDbContext ctx, string storeId, string eventId, bool checkCapacity = true)
    {
        var now = DateTime.UtcNow;
        var ticketEvent = ctx.Events.FirstOrDefault(c => c.StoreId == storeId && c.Id == eventId);
        if (ticketEvent == null || ticketEvent.EventState == Data.EntityState.Disabled || ticketEvent.StartDate.Date < now.Date || (ticketEvent.EndDate.HasValue && ticketEvent.EndDate.Value.Date < now.Date))
            return false;

        if (checkCapacity && ticketEvent.HasMaximumCapacity)
        {
            var totalTicketsSold = ctx.Orders.AsNoTracking().Where(c => c.StoreId == storeId && c.EventId == eventId && c.PaymentStatus == TransactionStatus.Settled.ToString())
                .SelectMany(c => c.Tickets).Count();

            if (totalTicketsSold >= ticketEvent.MaximumEventCapacity)
                return false;
        }
        return true;
    }

}
