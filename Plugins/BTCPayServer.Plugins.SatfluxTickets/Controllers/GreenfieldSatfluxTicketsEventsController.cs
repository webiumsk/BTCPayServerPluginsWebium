using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Plugins.SatfluxTickets.Services.Integration;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.SatfluxTickets.Controllers;

[Route("~/api/v1/stores/{storeId}/satflux-tickets/")]
[Route("~/api/v1/stores/{storeId}/satoshi-tickets/")] // legacy alias - existing satflux/WordPress integrations
[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield, Policy = Policies.CanModifyStoreSettings)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldSatfluxTicketsEventsController(StoreRepository storeRepo,
        IFileService fileService, UserManager<ApplicationUser> userManager, SimpleTicketSalesDbContextFactory dbContextFactory,
        RaffleEventBundleClientProvider raffleBundleProvider) : ControllerBase
{
    private IRaffleEventBundleClient? RaffleBundle => raffleBundleProvider.Client;

    [HttpGet("time-zones")]
    public IActionResult GetTimeZones(string storeId)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError) return storeError;
        return Ok(new { timeZones = EventTimeZoneService.TimeZones });
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(string storeId, [FromQuery] bool expired = false)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();

        var eventsQuery = ctx.Events.Where(c => c.StoreId == storeId);
        if (expired)
            eventsQuery = eventsQuery.Where(e => e.StartDate <= DateTime.UtcNow);

        var events = eventsQuery.ToList();
        if (!events.Any())
            return Ok(Array.Empty<EventData>());

        var eventIds = events.Select(e => e.Id).ToHashSet();
        var ticketSoldByEvent = ctx.Tickets
            .Where(t => t.StoreId == storeId && eventIds.Contains(t.EventId) && t.PaymentStatus == TransactionStatus.Settled.ToString())
            .GroupBy(t => t.EventId)
            .Select(g => new { EventId = g.Key, Count = g.Count() })
            .ToDictionary(x => x.EventId, x => x.Count);

        var result = await Task.WhenAll(events.Select(e => ToEventData(e, ticketSoldByEvent.GetValueOrDefault(e.Id, 0))));
        return Ok(result);
    }


    [HttpGet("events/{eventId}")]
    public async Task<IActionResult> GetEvent(string storeId, string eventId)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();

        var entity = ctx.Events.FirstOrDefault(c => c.Id == eventId && c.StoreId == storeId);
        if (entity == null)
            return EventNotFound();

        var ticketsSold = ctx.Tickets
            .Count(t => t.StoreId == storeId && t.EventId == eventId && t.PaymentStatus == TransactionStatus.Settled.ToString());

        return Ok(await ToEventData(entity, ticketsSold));
    }


    [HttpPost("events")]
    public async Task<IActionResult> CreateEvent(string storeId, [FromBody] ApiEventRequest request)
    {
        if (request == null)
        {
            ModelState.AddModelError(nameof(request), "Request body is required");
            return this.CreateValidationError(ModelState);
        }
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } createStoreError)
            return createStoreError;
        if (string.IsNullOrWhiteSpace(request.Title))
            ModelState.AddModelError(nameof(request.Title), "Title is required");
        if (!TicketRedirectUrlAttribute.IsAllowed(request.RedirectUrl))
            ModelState.AddModelError(nameof(request.RedirectUrl), TicketRedirectUrlAttribute.ValidationMessage);
        var time = EventTimeZoneService.Resolve(request);
        if (time.Errors.Count > 0) return TimeValidationError(time);
        if (time.StartDate.HasValue && time.StartDate.Value <= DateTime.UtcNow)
            ModelState.AddModelError(nameof(request.StartDate), "Event date cannot be in the past");

        await EventRaffleBundleRequestValidator.ApplyBundleFieldsAsync(
            ModelState, storeId, request.BundledRaffleTicketsPerAdmission ?? 0, request.BundledRaffleId, RaffleBundle);

        EventType parsedEventType = default;
        if (string.IsNullOrEmpty(request.EventType) || !Enum.TryParse<EventType>(request.EventType, true, out parsedEventType) || !Enum.IsDefined(parsedEventType))
            ModelState.AddModelError(nameof(request.EventType), "Invalid event type. Valid values: Virtual, Physical");

        if (!ModelState.IsValid)
            return this.CreateValidationError(ModelState);

        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        var currency = request.Currency;
        if (string.IsNullOrWhiteSpace(currency))
        {
            var store = await storeRepo.FindStore(storeId);
            currency = store?.GetStoreBlob()?.DefaultCurrency ?? "USD";
        }
        var entity = new Event
        {
            StoreId = storeId,
            Title = request.Title,
            Description = request.Description,
            Location = request.Location,
            StartDate = time.StartDate!.Value,
            EndDate = time.EndDate,
            TimeZone = time.TimeZone,
            Currency = currency.Trim().ToUpperInvariant(),
            RedirectUrl = request.RedirectUrl,
            EmailSubject = request.EmailSubject,
            EmailBody = request.EmailBody,
            EventType = parsedEventType,
            HasMaximumCapacity = false,
            EventState = Data.EntityState.Disabled,
            CreatedAt = DateTime.UtcNow
        };
        EventBundleHelper.ApplyBundleFields(entity, request.BundledRaffleTicketsPerAdmission ?? 0, request.BundledRaffleId);
        await using var ctx = dbContextFactory.CreateContext();
        ctx.Events.Add(entity);
        await ctx.SaveChangesAsync();
        return CreatedAtAction(nameof(GetEvent), new { storeId, eventId = entity.Id }, await ToEventData(entity, 0));
    }


    [HttpPut("events/{eventId}")]
    public async Task<IActionResult> UpdateEvent(string storeId, string eventId, [FromBody] ApiEventRequest request)
    {
        if (request == null)
        {
            ModelState.AddModelError(nameof(request), "Request body is required");
            return this.CreateValidationError(ModelState);
        }

        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();
        var entity = ctx.Events.FirstOrDefault(c => c.Id == eventId && c.StoreId == storeId);
        if (entity == null)
            return EventNotFound();

        if (request.Has(nameof(request.Title)) && string.IsNullOrWhiteSpace(request.Title))
            ModelState.AddModelError(nameof(request.Title), "Title is required");
        if (!TicketRedirectUrlAttribute.IsAllowed(request.RedirectUrl))
            ModelState.AddModelError(nameof(request.RedirectUrl), TicketRedirectUrlAttribute.ValidationMessage);
        var time = EventTimeZoneService.Resolve(request, entity);
        if (time.Errors.Count > 0) return TimeValidationError(time);
        if (time.StartDate.HasValue && time.StartDate.Value != entity.StartDate && time.StartDate.Value <= DateTime.UtcNow)
            ModelState.AddModelError(nameof(request.StartDate), "Event date cannot be in the past");
        if (request.Has(nameof(request.EventType)) && (string.IsNullOrEmpty(request.EventType)
            || !Enum.TryParse<EventType>(request.EventType, true, out var eventType) || !Enum.IsDefined(eventType)))
            ModelState.AddModelError(nameof(request.EventType), "Invalid event type. Valid values: Virtual, Physical");

        var bundlePerAdmission = request.BundledRaffleTicketsPerAdmission ?? entity.BundledRaffleTicketsPerAdmission;
        var bundleRaffleId = request.BundledRaffleId ?? entity.BundledRaffleId;
        if (request.BundledRaffleTicketsPerAdmission is 0)
        {
            bundlePerAdmission = 0;
            bundleRaffleId = null;
        }

        await EventRaffleBundleRequestValidator.ApplyBundleFieldsAsync(
            ModelState, storeId, bundlePerAdmission, bundleRaffleId, RaffleBundle);

        if (!ModelState.IsValid)
            return this.CreateValidationError(ModelState);

        if (request.Has(nameof(request.Title))) entity.Title = request.Title;
        if (request.Has(nameof(request.Description))) entity.Description = request.Description;
        if (request.Has(nameof(request.Location))) entity.Location = request.Location;
        entity.StartDate = time.StartDate!.Value;
        entity.EndDate = time.EndDate;
        entity.TimeZone = time.TimeZone;
        if (request.Has(nameof(request.RedirectUrl))) entity.RedirectUrl = request.RedirectUrl;
        if (request.Has(nameof(request.EmailSubject))) entity.EmailSubject = request.EmailSubject;
        if (request.Has(nameof(request.EmailBody))) entity.EmailBody = request.EmailBody;

        if (!string.IsNullOrEmpty(request.Currency))
            entity.Currency = request.Currency.Trim().ToUpperInvariant();

        if (!string.IsNullOrEmpty(request.EventType))
            entity.EventType = Enum.Parse<EventType>(request.EventType, true);

        if (request.BundledRaffleTicketsPerAdmission.HasValue || request.BundledRaffleId.HasValue)
            EventBundleHelper.ApplyBundleFields(entity, bundlePerAdmission, bundleRaffleId);

        await ctx.SaveChangesAsync();

        var ticketsSold = ctx.Tickets.Count(t => t.StoreId == storeId && t.EventId == eventId && t.PaymentStatus == TransactionStatus.Settled.ToString());
        return Ok(await ToEventData(entity, ticketsSold));
    }


    [HttpDelete("events/{eventId}")]
    public async Task<IActionResult> DeleteEvent(string storeId, string eventId)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = TicketTypeOrderService.CreateContext(dbContextFactory);
        await using var transaction = await ctx.Database.BeginTransactionAsync();
        var entity = await TicketTypeOrderService.LockEventAsync(ctx, storeId, eventId);
        if (entity == null)
            return EventNotFound();

        var tickets = ctx.Tickets.Where(c => c.StoreId == storeId && c.EventId == eventId).ToList();
        if (tickets.Any() && entity.StartDate > DateTime.UtcNow)
        {
            return this.CreateAPIError(422, "event-has-active-tickets", "Cannot delete event as there are active ticket purchases and the event is in the future");
        }
        var ticketTypes = ctx.TicketTypes.Where(c => c.EventId == eventId).ToList();
        if (tickets.Any())
            ctx.Tickets.RemoveRange(tickets);

        if (ticketTypes.Any())
            ctx.TicketTypes.RemoveRange(ticketTypes);

        ctx.Events.Remove(entity);
        await ctx.SaveChangesAsync();
        await transaction.CommitAsync();
        return Ok();
    }


    [HttpPut("events/{eventId}/toggle")]
    public async Task<IActionResult> ToggleEventStatus(string storeId, string eventId)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();
        var entity = ctx.Events.FirstOrDefault(c => c.Id == eventId && c.StoreId == storeId);
        if (entity == null)
            return EventNotFound();

        var ticketTypes = ctx.TicketTypes.Where(c => c.EventId == eventId).ToList();
        if (!ticketTypes.Any() && entity.EventState == Data.EntityState.Disabled)
        {
            return this.CreateAPIError(422, "no-ticket-types",
                "Cannot activate event without ticket types. Create at least one ticket type first.");
        }
        entity.EventState = entity.EventState == Data.EntityState.Active ? Data.EntityState.Disabled : Data.EntityState.Active;
        ctx.Events.Update(entity);
        await ctx.SaveChangesAsync();

        var ticketsSold = ctx.Tickets
            .Count(t => t.StoreId == storeId && t.EventId == eventId && t.PaymentStatus == TransactionStatus.Settled.ToString());

        return Ok(await ToEventData(entity, ticketsSold));
    }


    [HttpPost("events/{eventId}/logo")]
    public async Task<IActionResult> UploadEventLogo(string storeId, string eventId, IFormFile file)
    {
        if (file == null)
        {
            ModelState.AddModelError(nameof(file), "No file was uploaded");
            return this.CreateValidationError(ModelState);
        }

        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        if (!await IsSupportedLogoImage(file))
            return this.CreateAPIError(422, "logo-upload-failed", "Upload a JPEG, PNG, GIF or WebP image of at most 2 MiB.");

        await using var ctx = dbContextFactory.CreateContext();
        var entity = ctx.Events.FirstOrDefault(c => c.Id == eventId && c.StoreId == storeId);
        if (entity == null)
            return EventNotFound();

        var userId = userManager.GetUserId(User);
        var imageUpload = await fileService.UploadImage(file, userId, maxFileSizeInBytes: 2 * 1024 * 1024);
        if (!imageUpload.Success)
            return this.CreateAPIError(422, "logo-upload-failed", imageUpload.Response);

        entity.EventLogo = imageUpload.StoredFile.Id;
        ctx.Events.Update(entity);
        await ctx.SaveChangesAsync();
        var ticketsSold = ctx.Tickets.Count(t => t.StoreId == storeId && t.EventId == eventId && t.PaymentStatus == TransactionStatus.Settled.ToString());
        return Ok(await ToEventData(entity, ticketsSold));
    }

    [HttpDelete("events/{eventId}/logo")]
    public async Task<IActionResult> DeleteEventLogo(string storeId, string eventId)
    {
        if (GreenfieldStoreGuard.RequireStore(HttpContext, this, storeId) is { } storeError)
            return storeError;

        await using var ctx = dbContextFactory.CreateContext();

        var entity = ctx.Events.FirstOrDefault(c => c.Id == eventId && c.StoreId == storeId);
        if (entity == null)
            return EventNotFound();

        if (!string.IsNullOrEmpty(entity.EventLogo))
        {
            var userId = userManager.GetUserId(User);
            await fileService.RemoveFile(entity.EventLogo, userId);
        }
        entity.EventLogo = null;
        ctx.Events.Update(entity);
        await ctx.SaveChangesAsync();
        var ticketsSold = ctx.Tickets.Count(t => t.StoreId == storeId && t.EventId == eventId && t.PaymentStatus == TransactionStatus.Settled.ToString());
        return Ok(await ToEventData(entity, ticketsSold));
    }

    private static async Task<bool> IsSupportedLogoImage(IFormFile file)
    {
        if (file.Length is <= 0 or > 2 * 1024 * 1024)
            return false;

        // Restrict content to the raster formats supported by Satflux; MIME alone is untrusted.
        var header = new byte[12];
        await using var stream = file.OpenReadStream();
        var length = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
        return file.ContentType.ToLowerInvariant() switch
        {
            "image/png" => length >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => length >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255,
            "image/gif" => length >= 6 && (header.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || header.AsSpan(0, 6).SequenceEqual("GIF89a"u8)),
            "image/webp" => length >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
    }

    private async Task<EventData> ToEventData(Event entity, int ticketsSold)
    {
        var eventLogoUrl = string.IsNullOrEmpty(entity.EventLogo)
            ? null
            : await fileService.GetFileUrl(Request.GetAbsoluteRootUri(), entity.EventLogo);

        return new EventData
        {
            Id = entity.Id,
            StoreId = entity.StoreId,
            EventLogoFileId = entity.EventLogo ?? string.Empty,
            EventLogoUrl = eventLogoUrl,
            Title = entity.Title,
            Description = entity.Description,
            EventType = entity.EventType.ToString(),
            Location = entity.Location,
            StartDate = entity.StartDate,
            TimeZone = EventTimeZoneService.StoredId(entity.TimeZone),
            EndDate = entity.EndDate,
            Currency = entity.Currency,
            TicketsSold = ticketsSold,
            RedirectUrl = entity.RedirectUrl,
            EmailSubject = entity.EmailSubject,
            EmailBody = entity.EmailBody,
            HasMaximumCapacity = entity.HasMaximumCapacity,
            MaximumEventCapacity = entity.MaximumEventCapacity,
            EventState = entity.EventState.ToString(),
            CreatedAt = entity.CreatedAt,
            BundledRaffleId = entity.BundledRaffleId,
            BundledRaffleTicketsPerAdmission = entity.BundledRaffleTicketsPerAdmission,
            PurchaseLink = entity.EventState == Data.EntityState.Disabled ? null : Url.Action(nameof(UITicketSalesPublicController.EventSummary), "UITicketSalesPublic",
                new { storeId = entity.StoreId, eventId = entity.Id }, Request.Scheme)
        };
    }

    private IActionResult TimeValidationError(EventTimeZoneService.Resolution time)
    {
        var error = time.Errors.First();
        var prefix = error.Key.StartsWith("end", StringComparison.Ordinal) ? "event-end-local-" : "event-start-local-";
        var code = error.Value switch
        {
            "TimeZoneRequired" or "TimeZoneInvalid" => "event-time-zone-invalid",
            "CivilMissing" => prefix + "nonexistent",
            "CivilAmbiguous" => prefix + "ambiguous",
            "TimeConflict" => "event-date-conflict",
            "EndBefore" => "event-date-range-invalid",
            _ => prefix + "invalid"
        };
        return this.CreateAPIError(422, code, new TicketSalesText("en")[error.Value]);
    }

    private IActionResult EventNotFound()
    {
        return this.CreateAPIError(404, "event-not-found", "The event was not found");
    }
}
