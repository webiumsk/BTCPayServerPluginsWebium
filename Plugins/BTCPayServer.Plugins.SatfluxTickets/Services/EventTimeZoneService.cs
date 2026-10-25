#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>UTC instants are authoritative; this service owns explicit civil-time conversion and venue presentation.</summary>
public static class EventTimeZoneService
{
    // Match BTCPay's native picker, including the operating system's native IDs.
    public static IReadOnlyList<string> TimeZones { get; } = Array.AsReadOnly(TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => zone.Id).Order(StringComparer.Ordinal).ToArray());
    private static bool SafeId(string? id) => id is { Length: > 0 and <= 100 }
        && id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '/' or '_' or '-' or '+' or ' ' or '(' or ')' or '.')
        && !id.Contains("..", StringComparison.Ordinal);
    public static bool TryGet(string? id, out TimeZoneInfo zone)
    {
        zone = null!;
        return SafeId(id) && global::BTCPayServer.TimeZones.TryGet(id!, out zone!);
    }
    // Compatibility for metadata written by 2.2.1. IANA's backward file links
    // Europe/Bratislava to Europe/Prague; this is never an extra authoring choice.
    public static bool TryGetStored(string? id, out TimeZoneInfo zone)
    {
        if (string.Equals(id, "Europe/Bratislava", StringComparison.Ordinal)
            && TryGet("Europe/Prague", out zone)) return true;
        return TryGet(id, out zone);
    }
    public static string? StoredId(string? id) => TryGetStored(id, out var zone) ? zone.Id : id;
    public static DateTime Utc(DateTime instant) => instant.Kind == DateTimeKind.Local
        ? instant.ToUniversalTime() : DateTime.SpecifyKind(instant, DateTimeKind.Utc);
    public static string Civil(DateTime instant, string? zone) => TimeZoneInfo.ConvertTimeFromUtc(Utc(instant),
        zone == null ? TimeZoneInfo.Utc : TryGetStored(zone, out var selected) ? selected
            : throw new ArgumentException("A supported time zone is required.", nameof(zone))).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    public static bool TryCivil(string? value, string? zoneId, out DateTime instant, out string? error)
    {
        instant = default; error = null;
        if (!TryGet(zoneId, out var zone)) { error = "TimeZoneRequired"; return false; }
        if (value is not { Length: > 0 and <= 40 } || !DateTime.TryParseExact(value, new[] { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var civil)) { error = "CivilInvalid"; return false; }
        civil = DateTime.SpecifyKind(civil, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(civil)) { error = "CivilMissing"; return false; }
        if (zone.IsAmbiguousTime(civil)) { error = "CivilAmbiguous"; return false; }
        instant = TimeZoneInfo.ConvertTimeToUtc(civil, zone);
        return true;
    }
    public static DateTimeOffset AtVenue(DateTime instant, string zoneId)
    {
        if (!TryGetStored(zoneId, out var zone)) throw new ArgumentException("A supported time zone is required.", nameof(zoneId));
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(Utc(instant)), zone);
    }
    public static string VenueDate(DateTime instant, string zoneId, CultureInfo culture)
    {
        var local = AtVenue(instant, zoneId);
        return local.ToString("f", culture) + " (UTC" + local.ToString("zzz", CultureInfo.InvariantCulture) + ", " + StoredId(zoneId) + ")";
    }
    public static string EmailDate(Event ev)
    {
        if (ev.EventType == EventType.Physical && ev.TimeZone != null)
        {
            if (TryGetStored(ev.TimeZone, out _))
                return AtVenue(ev.StartDate, ev.TimeZone).DateTime.ToString("MMMM dd, yyyy", CultureInfo.CurrentCulture);
            // Delivery must remain possible for recoverable legacy metadata. Label
            // the authoritative UTC date explicitly rather than guessing a venue offset.
            return Utc(ev.StartDate).ToString("MMMM dd, yyyy", CultureInfo.CurrentCulture) + " UTC - "
                + new TicketSalesText(CultureInfo.CurrentCulture.Name)["MissingVenueTimezone"];
        }
        return Utc(ev.StartDate).ToString("MMMM dd, yyyy", CultureInfo.CurrentCulture);
    }

    public sealed record Resolution(DateTime? StartDate, DateTime? EndDate, string? TimeZone, IReadOnlyDictionary<string, string> Errors);
    public static Resolution Resolve(ApiEventRequest request, Event? current = null)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var zone = StoredId(current?.TimeZone);
        if (request.TimeZone != null)
        {
            if (!TryGet(request.TimeZone, out var selected)) errors["timeZone"] = "TimeZoneInvalid";
            else zone = selected.Id;
        }
        else if (current?.EventType == EventType.Physical && zone != null && !TryGet(zone, out _))
            errors["timeZone"] = "TimeZoneInvalid";
        DateTime? start = current?.StartDate, end = current?.EndDate;
        var startLocal = request.Has(nameof(request.StartLocal)); var startInstant = request.Has(nameof(request.StartDate));
        var endLocal = request.Has(nameof(request.EndLocal)); var endInstant = request.Has(nameof(request.EndDate));
        if (startLocal && startInstant) errors["startLocal"] = "TimeConflict";
        else if (startLocal)
        {
            if (request.TimeZone == null) errors["startLocal"] = "TimeZoneRequired";
            else if (!TryCivil(request.StartLocal, zone, out var converted, out var error)) errors["startLocal"] = error!;
            else start = converted;
        }
        else if (startInstant)
        {
            if (!request.StartDate.HasValue) errors["startDate"] = "StartRequired";
            else if (request.StartDate.Value.Kind == DateTimeKind.Unspecified) errors["startDate"] = "InstantOffsetRequired";
            else start = Utc(request.StartDate.Value);
        }
        if (!start.HasValue) errors.TryAdd("startDate", "StartRequired");
        if (endLocal && endInstant) errors["endLocal"] = "TimeConflict";
        else if (endLocal)
        {
            if (request.EndLocal == null) end = null;
            else if (request.TimeZone == null) errors["endLocal"] = "TimeZoneRequired";
            else if (!TryCivil(request.EndLocal, zone, out var converted, out var error)) errors["endLocal"] = error!;
            else end = converted;
        }
        else if (endInstant)
        {
            if (!request.EndDate.HasValue) end = null;
            else if (request.EndDate.Value.Kind == DateTimeKind.Unspecified) errors["endDate"] = "InstantOffsetRequired";
            else end = Utc(request.EndDate.Value);
        }
        if (start.HasValue && end.HasValue && end.Value < start.Value) errors["endLocal"] = "EndBefore";
        return new(start, end, zone, errors);
    }
}
