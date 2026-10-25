using System.Globalization;
using BTCPayServer;
using System.Reflection;
using System.Text.Json;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Data;
using BTCPayServer.Plugins.SatfluxTickets.Controllers;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Plugins.SatfluxTickets.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class EventTimeZoneTests
{
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("Europe/Prague\r\nX: 1")]
    [InlineData("Europe/Unknown")]
    [InlineData("")]
    public void Unapproved_identifiers_are_rejected_before_conversion(string zone) => Assert.False(EventTimeZoneService.TryGet(zone, out _));

    [Fact]
    public void Oversized_and_control_character_identifiers_are_rejected()
    {
        Assert.False(EventTimeZoneService.TryGet(new string('a', 101), out _));
        Assert.False(EventTimeZoneService.TryGet(null, out _));
        Assert.False(EventTimeZoneService.TryGet("UTC\0", out _));
        Assert.False(EventTimeZoneService.TryGet("UTC\t", out _));
    }

    [Fact]
    public void Catalog_matches_BTCPay_native_picker_exactly()
    {
        Assert.Equal(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Order(StringComparer.Ordinal), EventTimeZoneService.TimeZones);
        Assert.All(EventTimeZoneService.TimeZones, id => Assert.True(EventTimeZoneService.TryGet(id, out _), id));
    }

    [Theory]
    [InlineData("europe/prague", "Europe/Prague")]
    [InlineData("CET", "Europe/Paris")]
    [InlineData("Etc/UTC", "UTC")]
    public void Native_resolver_case_and_abbreviations_persist_the_host_canonical_ID(string input, string expected)
    {
        Assert.True(global::BTCPayServer.TimeZones.TryGet(input, out var native));
        Assert.True(EventTimeZoneService.TryGet(input, out var actual));
        Assert.Equal(native.Id, actual.Id);
        var result = EventTimeZoneService.Resolve(new() { TimeZone = input, StartDate = DateTime.UtcNow });
        Assert.Empty(result.Errors); Assert.Equal(expected, result.TimeZone);
    }

    [Fact]
    public void Legacy_alias_is_read_compatible_but_never_injected_as_an_authoring_choice()
    {
        Assert.Equal(global::BTCPayServer.TimeZones.TryGet("Europe/Bratislava", out _), EventTimeZoneService.TryGet("Europe/Bratislava", out _));
        Assert.Equal(TimeZoneInfo.GetSystemTimeZones().Any(z => z.Id == "Europe/Bratislava"), EventTimeZoneService.TimeZones.Contains("Europe/Bratislava"));
        Assert.Equal("Europe/Prague", EventTimeZoneService.StoredId("Europe/Bratislava"));
        foreach (var instant in new[] { new DateTime(2026, 11, 28, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 28, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026,10,25,0,30,0,DateTimeKind.Utc), new DateTime(2026,10,25,1,30,0,DateTimeKind.Utc) })
            Assert.Equal(EventTimeZoneService.Civil(instant, "Europe/Prague"), EventTimeZoneService.Civil(instant, "Europe/Bratislava"));
        if (!global::BTCPayServer.TimeZones.TryGet("Europe/Bratislava", out _))
            Assert.Contains("timeZone", EventTimeZoneService.Resolve(new() { TimeZone = "Europe/Bratislava", StartDate = DateTime.UtcNow }).Errors.Keys);
    }

    [Fact]
    public void Unknown_physical_zone_is_not_silently_presented_as_UTC()
    {
        var ev = new Event { EventType = EventType.Physical, TimeZone = "Europe/Unknown", StartDate = DateTime.UtcNow };
        Assert.Throws<ArgumentException>(() => EventTimeZoneService.Civil(ev.StartDate, ev.TimeZone));
        Assert.Contains(" UTC", EventTimeZoneService.EmailDate(ev));
        Assert.Contains(new TicketSalesText(CultureInfo.CurrentCulture.Name)["MissingVenueTimezone"], EventTimeZoneService.EmailDate(ev));
        Assert.Contains("timeZone", EventTimeZoneService.Resolve(new() { Title = "Changed" }, ev).Errors.Keys);
        var controller = NativeController();
        var map = typeof(UITicketSalesController).GetMethod("TicketSalesEventToViewModel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var vm = (UpdateSimpleTicketSalesEventViewModel)map.Invoke(controller, new object[] { ev })!;
        Assert.Null(vm.StartLocal); Assert.NotEmpty(controller.ModelState[nameof(vm.TimeZone)]!.Errors);
    }

    [Fact]
    public void Legacy_native_edit_canonicalizes_visible_metadata_but_preserves_raw_baseline_and_instants()
    {
        var controller = NativeController();
        var map = typeof(UITicketSalesController).GetMethod("TicketSalesEventToViewModel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var guard = typeof(UITicketSalesController).GetMethod("NativeScheduleMatches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var apply = typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var save = typeof(UITicketSalesController).GetMethod("TicketSalesEventViewModelToEntity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ev = new Event { Id = "synthetic", EventType = EventType.Physical, TimeZone = "Europe/Bratislava", StartDate = new DateTime(2026,10,25,0,30,0,DateTimeKind.Utc).AddTicks(1234560), ReminderSentAt = DateTimeOffset.UtcNow };
        var ticks = ev.StartDate.Ticks; var reminder = ev.ReminderSentAt;
        var vm = (UpdateSimpleTicketSalesEventViewModel)map.Invoke(controller, new object[] { ev })!;
        Assert.Equal("Europe/Prague", vm.TimeZone); Assert.Equal("Europe/Bratislava", vm.OriginalTimeZone); Assert.Equal("Europe/Bratislava", ev.TimeZone);
        Assert.True((bool)guard.Invoke(controller, new object[] { vm, ev })!);
        Assert.True((bool)apply.Invoke(controller, new object[] { vm, ev })!);
        save.Invoke(controller, new object[] { vm, ev, "synthetic-store" });
        Assert.Equal("Europe/Prague", ev.TimeZone); Assert.Equal(ticks, ev.StartDate.Ticks); Assert.Equal(reminder, ev.ReminderSentAt);
        Assert.False((bool)guard.Invoke(controller, new object[] { vm, ev })!); // Raw metadata changed since the form baseline.
    }

    [Theory]
    [InlineData("2026-11-28T09:00:00", "Europe/Prague", "2026-11-28T08:00:00Z")]
    [InlineData("2026-06-28T09:00", "Europe/Prague", "2026-06-28T07:00:00Z")]
    [InlineData("2026-11-28T09:00:00", "Asia/Kathmandu", "2026-11-28T03:15:00Z")]
    [InlineData("2026-11-28T09:00:00", "Australia/Lord_Howe", "2026-11-27T22:00:00Z")]
    public void Civil_time_conversion_uses_venue_rules_not_machine_or_visitor_zone(string civil, string zone, string expected)
    {
        Assert.True(EventTimeZoneService.TryCivil(civil, zone, out var actual, out var error));
        Assert.Null(error); Assert.Equal(DateTime.Parse(expected, null, DateTimeStyles.RoundtripKind), actual);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
    }

    [Theory]
    [InlineData("2026-03-29T02:30:00", "CivilMissing")]
    [InlineData("2026-10-25T02:30:00", "CivilAmbiguous")]
    [InlineData("2026-02-30T09:00:00", "CivilInvalid")]
    [InlineData("2026-11-28T09:00:00+01:00", "CivilInvalid")]
    public void Nonexistent_ambiguous_and_offset_civil_dates_are_rejected(string civil, string expected)
    {
        Assert.False(EventTimeZoneService.TryCivil(civil, "Europe/Prague", out _, out var error)); Assert.Equal(expected, error);
    }

    [Fact]
    public void Presence_tracking_distinguishes_missing_end_from_explicit_null_for_both_json_stacks()
    {
        foreach (var request in new[] { JsonSerializer.Deserialize<ApiEventRequest>("{}")!, Newtonsoft.Json.JsonConvert.DeserializeObject<ApiEventRequest>("{}")! })
            Assert.False(request.Has(nameof(request.EndLocal)));
        foreach (var request in new[] { JsonSerializer.Deserialize<ApiEventRequest>("{\"EndLocal\":null}")!, Newtonsoft.Json.JsonConvert.DeserializeObject<ApiEventRequest>("{\"endLocal\":null}")! })
            Assert.True(request.Has(nameof(request.EndLocal)));
    }

    [Fact]
    public void Metadata_only_zone_update_preserves_exact_instants_and_omitted_end_but_explicit_null_clears_it()
    {
        var current = new Event { StartDate = new DateTime(2026, 11, 28, 8, 0, 0, DateTimeKind.Utc).AddTicks(1234560), EndDate = new DateTime(2026, 11, 29, 11, 0, 0, DateTimeKind.Utc), TimeZone = "Europe/Prague" };
        var result = EventTimeZoneService.Resolve(new() { TimeZone = "America/New_York" }, current);
        Assert.Empty(result.Errors); Assert.Equal(current.StartDate.Ticks, result.StartDate!.Value.Ticks); Assert.Equal(current.EndDate, result.EndDate);
        Assert.Null(EventTimeZoneService.Resolve(new() { EndLocal = null! }, current).EndDate);
        Assert.Null(EventTimeZoneService.Resolve(new() { EndDate = null }, current).EndDate);
        Assert.Equal(current.TimeZone, EventTimeZoneService.Resolve(new() { TimeZone = null! }, current).TimeZone);
        Assert.Contains("timeZone", EventTimeZoneService.Resolve(new() { TimeZone = "" }, current).Errors.Keys);
    }

    [Fact]
    public void UTC_API_can_express_fold_instants_but_local_API_requires_explicit_zone_and_rejects_conflicts()
    {
        var firstFold = DateTime.Parse("2026-10-25T00:30:00Z", null, DateTimeStyles.RoundtripKind);
        Assert.Empty(EventTimeZoneService.Resolve(new() { StartDate = firstFold, TimeZone = "Europe/Prague" }).Errors);
        Assert.Contains("startLocal", EventTimeZoneService.Resolve(new() { StartDate = firstFold, StartLocal = "2026-10-25T02:30:00", TimeZone = "Europe/Prague" }).Errors.Keys);
        Assert.Equal("TimeZoneRequired", EventTimeZoneService.Resolve(new() { StartLocal = "2026-11-28T09:00:00" }).Errors["startLocal"]);
        Assert.Equal("InstantOffsetRequired", EventTimeZoneService.Resolve(new() { StartDate = DateTime.SpecifyKind(firstFold, DateTimeKind.Unspecified) }).Errors["startDate"]);
        Assert.Null(EventTimeZoneService.Resolve(new() { StartDate = firstFold }).TimeZone); // future old-client creates do not inherit migration metadata
    }

    [Theory]
    [InlineData("en")]
    [InlineData("sk")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("cs")]
    public void Server_venue_display_contains_real_event_offset_in_every_language(string language)
    {
        var date = new DateTime(2026, 11, 28, 8, 0, 0, DateTimeKind.Utc);
        var rendered = EventTimeZoneService.VenueDate(date, "Europe/Prague", new TicketSalesText(language).Culture);
        Assert.Contains("UTC+01:00", rendered); Assert.Contains("Europe/Prague", rendered);
        Assert.Equal(9, EventTimeZoneService.AtVenue(date, "Europe/Prague").Hour);
        Assert.Contains("UTC+02:00", EventTimeZoneService.VenueDate(date.AddMonths(-5), "Europe/Prague", new TicketSalesText(language).Culture));
    }

    [Fact]
    public void Email_event_date_uses_physical_venue_calendar_day_but_preserves_virtual_UTC_date()
    {
        var current = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
            var ev = new Event { EventType = EventType.Physical, TimeZone = "Europe/Prague", StartDate = new DateTime(2026, 11, 27, 23, 30, 0, DateTimeKind.Utc) };
            Assert.Equal("November 28, 2026", EventTimeZoneService.EmailDate(ev));
            ev.EventType = EventType.Virtual; Assert.Equal("November 27, 2026", EventTimeZoneService.EmailDate(ev));
        }
        finally { CultureInfo.CurrentCulture = current; }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("sk")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("cs")]
    public void Unsupported_venue_zone_preserves_ticket_email_content_and_links_with_a_localized_explicit_UTC_warning(string language)
    {
        var current = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
            var ev = new Event { Title = "Synthetic concert", EventType = EventType.Physical, TimeZone = "Europe/Unknown",
                StartDate = new DateTime(2026,11,27,23,30,0,DateTimeKind.Utc) };
            var ticket = new Ticket { FirstName = "Synthetic", LastName = "Buyer", Email = "buyer@example.test",
                TicketNumber = "EVT-0007-261020-ABC123", QRCodeLink = "https://tickets.example.test/ticket/ABC123" };
            var apply = typeof(EmailService).GetMethod("ApplyTemplate", BindingFlags.Static | BindingFlags.NonPublic)!;
            var warning = new TicketSalesText(language)["MissingVenueTimezone"];
            foreach (var template in new[] { "{{Title}} {{Name}} {{EventDate}}", "{{Title}} {{Name}}" })
            {
                // ApplyTemplate eagerly evaluates EmailDate even without a placeholder.
                var body = (string)apply.Invoke(null, new object[] { template, ev, ticket })!;
                Assert.Contains("Synthetic concert Synthetic Buyer", body);
                if (template.Contains("{{EventDate}}", StringComparison.Ordinal))
                {
                    Assert.Contains(ev.StartDate.ToString("MMMM dd, yyyy", CultureInfo.CurrentCulture) + " UTC", body);
                    Assert.Contains(warning, body);
                }
                using var message = TicketEmailMessageBuilder.Create(new BTCPayServer.Plugins.Emails.Services.EmailSettings { From = "tickets@example.test" },
                    MimeKit.MailboxAddress.Parse(ticket.Email), "Synthetic tickets", body, ticket.TicketNumber, ticket.QRCodeLink);
                Assert.Equal(ticket.Email, Assert.IsType<MimeKit.MailboxAddress>(Assert.Single(message.To)).Address);
                Assert.Contains("Synthetic concert Synthetic Buyer", message.TextBody); Assert.Contains(ticket.QRCodeLink, message.TextBody);
                Assert.Contains("ABC123", message.TextBody); Assert.Contains(ticket.QRCodeLink, message.HtmlBody);
            }
        }
        finally { CultureInfo.CurrentCulture = current; }
    }

    [Fact]
    public void Native_zone_only_edit_preserves_subsecond_UTC_and_ambiguous_existing_instant()
    {
        var controller = NativeController();
        var apply = typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = new Event { EventType = EventType.Physical, TimeZone = "Europe/Prague", StartDate = new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc).AddTicks(1234560) };
        var vm = new UpdateSimpleTicketSalesEventViewModel { EventType = EventType.Physical, TimeZone = "america/new_york", StartLocal = EventTimeZoneService.Civil(original.StartDate, original.TimeZone) };
        Assert.True((bool)apply.Invoke(controller, new object[] { vm, original })!); Assert.Equal(original.StartDate.Ticks, vm.StartDate.Ticks);
        Assert.Equal("America/New_York", vm.TimeZone);
        vm.TimeZone = "Europe/Prague"; vm.StartEdited = true;
        Assert.False((bool)apply.Invoke(controller, new object[] { vm, original })!); Assert.NotEmpty(controller.ModelState[nameof(vm.StartLocal)]!.Errors);
    }

    [Fact]
    public void Native_virtual_editor_zone_is_validated_and_does_not_replace_persisted_venue_metadata()
    {
        var controller = NativeController();
        var apply = typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = typeof(UITicketSalesController).GetMethod("TicketSalesEventViewModelToEntity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = new Event { Id = "synthetic", EventType = EventType.Physical, TimeZone = "Europe/Prague", StartDate = DateTime.UtcNow };
        var vm = new UpdateSimpleTicketSalesEventViewModel { EventType = EventType.Virtual, TimeZone = "America/New_York", EditorTimeZone = "Asia/Kathmandu", StartLocal = "2027-11-28T09:00:00", StartEdited = true };
        Assert.True((bool)apply.Invoke(controller, new object[] { vm, original })!);
        var mapped = (Event)map.Invoke(controller, new object[] { vm, original, "synthetic-store" })!;
        Assert.Equal("Europe/Prague", mapped.TimeZone); Assert.Equal(3, mapped.StartDate.Hour); Assert.Equal(15, mapped.StartDate.Minute);
        vm.EditorTimeZone = "Europe/Unknown"; Assert.False((bool)apply.Invoke(controller, new object[] { vm, original })!);
    }

    [Fact]
    public void Native_zone_only_mapping_preserves_capacity_opaque_logo_reminders_and_exact_UTC_instants()
    {
        var controller = NativeController();
        var apply = typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = typeof(UITicketSalesController).GetMethod("TicketSalesEventViewModelToEntity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = new Event
        {
            Id = "synthetic-existing", StoreId = "owned-store", EventType = EventType.Physical, TimeZone = "Europe/Prague",
            HasMaximumCapacity = true, MaximumEventCapacity = 42, EventLogo = "opaque-file-id", Title = "Synthetic", Currency = "EUR",
            StartDate = new DateTime(2027,11,28,8,0,0,DateTimeKind.Utc).AddTicks(1234560),
            EndDate = new DateTime(2027,11,29,11,0,0,DateTimeKind.Utc).AddTicks(6543210),
            ReminderSentAt = new DateTimeOffset(2026,10,1,1,0,0,TimeSpan.Zero)
        };
        var startTicks = original.StartDate.Ticks; var endTicks = original.EndDate.Value.Ticks; var reminder = original.ReminderSentAt;
        var vm = new UpdateSimpleTicketSalesEventViewModel
        {
            EventType = EventType.Physical, TimeZone = "America/New_York", Title = original.Title, Currency = original.Currency,
            StartLocal = EventTimeZoneService.Civil(original.StartDate, original.TimeZone),
            EndLocal = EventTimeZoneService.Civil(original.EndDate.Value, original.TimeZone),
            EventImageUrl = "https://example.invalid/resolved-image/opaque-file-id"
        };
        Assert.True((bool)apply.Invoke(controller, new object[] { vm, original })!);
        var mapped = (Event)map.Invoke(controller, new object[] { vm, original, original.StoreId })!;
        Assert.Equal("America/New_York", mapped.TimeZone); Assert.Equal(startTicks, mapped.StartDate.Ticks); Assert.Equal(endTicks, mapped.EndDate!.Value.Ticks);
        Assert.Equal(reminder, mapped.ReminderSentAt); Assert.True(mapped.HasMaximumCapacity); Assert.Equal(42, mapped.MaximumEventCapacity);
        Assert.Equal("opaque-file-id", mapped.EventLogo);
    }

    [Fact]
    public void Fresh_native_schedule_baseline_preserves_precision_and_allows_manual_no_javascript_edits()
    {
        var controller = NativeController();
        var guard = typeof(UITicketSalesController).GetMethod("NativeScheduleMatches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var apply = typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = new Event { EventType = EventType.Physical, TimeZone = "Europe/Prague",
            StartDate = new DateTime(2027,11,28,8,0,0,DateTimeKind.Utc).AddTicks(1234560),
            EndDate = new DateTime(2027,11,29,11,0,0,DateTimeKind.Utc).AddTicks(6543210) };
        var vm = new UpdateSimpleTicketSalesEventViewModel { EventType = EventType.Physical, TimeZone = "Europe/Prague",
            OriginalTimeZone = original.TimeZone, OriginalEventType = original.EventType,
            OriginalStartUtc = original.StartDate.ToString("O", CultureInfo.InvariantCulture),
            OriginalEndUtc = original.EndDate.Value.ToString("O", CultureInfo.InvariantCulture),
            StartLocal = EventTimeZoneService.Civil(original.StartDate, original.TimeZone), EndLocal = EventTimeZoneService.Civil(original.EndDate.Value, original.TimeZone) };
        Assert.True((bool)guard.Invoke(controller, new object[] { vm, original })!);
        Assert.True((bool)apply.Invoke(controller, new object[] { vm, original })!);
        Assert.Equal(original.StartDate.Ticks, vm.StartDate.Ticks); Assert.Equal(original.EndDate.Value.Ticks, vm.EndDate!.Value.Ticks);
        // Native no-JavaScript edits do not set StartEdited; changed civil text still converts deliberately.
        vm.StartLocal = "2027-11-28T11:00:00";
        Assert.True((bool)guard.Invoke(controller, new object[] { vm, original })!);
        Assert.True((bool)apply.Invoke(controller, new object[] { vm, original })!); Assert.Equal(10, vm.StartDate.Hour);
        vm.OriginalStartUtc = new DateTimeOffset(original.StartDate).ToOffset(TimeSpan.FromHours(1)).ToString("O", CultureInfo.InvariantCulture);
        Assert.True((bool)guard.Invoke(controller, new object[] { vm, original })!);
    }

    [Fact]
    public void Fresh_no_javascript_zone_only_edit_preserves_instants_but_missing_type_requires_reload()
    {
        var controller = NativeController();
        var guard = typeof(UITicketSalesController).GetMethod("NativeScheduleMatches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var apply = typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = new Event { EventType = EventType.Physical, TimeZone = "Europe/Prague",
            StartDate = new DateTime(2027,11,28,8,0,0,DateTimeKind.Utc).AddTicks(1234560) };
        var vm = new UpdateSimpleTicketSalesEventViewModel { EventType = EventType.Physical, TimeZone = "UTC",
            OriginalTimeZone = original.TimeZone, OriginalEventType = original.EventType,
            OriginalStartUtc = original.StartDate.ToString("O", CultureInfo.InvariantCulture),
            StartLocal = EventTimeZoneService.Civil(original.StartDate, original.TimeZone), StartEdited = false };
        Assert.True((bool)guard.Invoke(controller, new object[] { vm, original })!);
        Assert.True((bool)apply.Invoke(controller, new object[] { vm, original })!); Assert.Equal(original.StartDate.Ticks, vm.StartDate.Ticks);
        vm.OriginalEventType = null;
        Assert.False((bool)guard.Invoke(controller, new object[] { vm, original })!);
        Assert.NotEmpty(controller.ModelState[nameof(vm.StartLocal)]!.Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2027-11-28T08:00:00.0000000")]
    [InlineData("not-a-date")]
    public void Native_missing_or_unqualified_baseline_requires_reload_instead_of_reinterpreting_old_civil_time(string? baseline)
    {
        var controller = NativeController();
        var guard = typeof(UITicketSalesController).GetMethod("NativeScheduleMatches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = new Event { StartDate = new DateTime(2027,11,28,8,0,0,DateTimeKind.Utc) };
        var vm = new UpdateSimpleTicketSalesEventViewModel { OriginalTimeZone = original.TimeZone, OriginalEventType = original.EventType, OriginalStartUtc = baseline!, StartLocal = "2027-11-28T09:00:00", TimeZone = "Europe/Prague" };
        Assert.False((bool)guard.Invoke(controller, new object[] { vm, original })!);
        Assert.Equal(new TicketSalesText("en")["NativeScheduleChanged"], controller.ModelState[nameof(vm.StartLocal)]!.Errors.Single().ErrorMessage);
        Assert.Equal("2027-11-28T09:00:00", vm.StartLocal);
    }

    [Fact]
    public void Native_unknown_event_type_cannot_bypass_the_physical_timezone_requirement()
    {
        var controller = NativeController();
        var apply = typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var vm = new UpdateSimpleTicketSalesEventViewModel { EventType = (EventType)99, EditorTimeZone = "UTC", StartLocal = "2027-11-28T09:00:00" };
        Assert.False((bool)apply.Invoke(controller, new object[] { vm, null! })!);
        Assert.NotEmpty(controller.ModelState[nameof(vm.EventType)]!.Errors);
    }

    [Fact]
    public void Catalog_requires_the_same_store_authorization_as_event_operations()
    {
        var controller = new GreenfieldSatfluxTicketsEventsController(null!, null!, null!, null!, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.SetStoreData(new StoreData { Id = "owned-store" });
        Assert.IsType<OkObjectResult>(controller.GetTimeZones("owned-store"));
        Assert.IsNotType<OkObjectResult>(controller.GetTimeZones("foreign-store"));
    }
    private static UITicketSalesController NativeController() => new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
    { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
}
