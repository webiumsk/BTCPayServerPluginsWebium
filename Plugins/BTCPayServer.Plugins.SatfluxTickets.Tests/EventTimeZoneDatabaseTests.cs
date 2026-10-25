using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.SatfluxTickets.Controllers;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Plugins.SatfluxTickets.Services.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using BTCPayServer.Plugins.SatfluxTickets.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

[Collection("Tickets disposable PostgreSQL")]
public class EventTimeZoneDatabaseTests
{
    private static SimpleTicketSalesDbContextFactory Factory()
    {
        var connection = Environment.GetEnvironmentVariable("TICKETS_ORDER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        Assert.Contains(parsed.Host, new[] { "localhost", "127.0.0.1", "::1" });
        Assert.StartsWith("satflux_tickets_ordering_", parsed.Database);
        return new(Options.Create(new DatabaseOptions { ConnectionString = connection! }));
    }
    private static GreenfieldSatfluxTicketsEventsController Controller(SimpleTicketSalesDbContextFactory factory, string store)
    {
        var controller = new GreenfieldSatfluxTicketsEventsController(null!, null!, null!, factory,
            new RaffleEventBundleClientProvider(new ServiceCollection().BuildServiceProvider()))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.SetStoreData(new BTCPayServer.Data.StoreData { Id = store });
        return controller;
    }
    private static async Task<(SimpleTicketSalesDbContextFactory Factory, Event Event)> Seed(string timeZone = "Europe/Prague")
    {
        var factory = Factory(); await using var ctx = factory.CreateContext(); await ctx.Database.MigrateAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var ev = new Event { Id = "zone-event-" + suffix, StoreId = "zone-store-" + suffix, Title = "Synthetic zone event", EventType = EventType.Physical,
            EventState = Data.EntityState.Disabled, Currency = "EUR", TimeZone = timeZone,
            StartDate = new DateTime(2035, 11, 28, 8, 0, 0, DateTimeKind.Utc).AddTicks(1234560),
            EndDate = new DateTime(2035, 11, 29, 11, 0, 0, DateTimeKind.Utc).AddTicks(6543210),
            ReminderSentAt = new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero), CreatedAt = DateTimeOffset.UtcNow };
        ctx.Events.Add(ev); await ctx.SaveChangesAsync(); return (factory, ev);
    }

    [OrderDatabaseFact]
    public async Task Legacy_API_read_and_title_only_update_normalize_metadata_without_rewriting_instants_or_reminders()
    {
        var (factory, original) = await Seed("Europe/Bratislava"); var controller = Controller(factory, original.StoreId);
        var read = Assert.IsType<EventData>(Assert.IsType<OkObjectResult>(await controller.GetEvent(original.StoreId, original.Id)).Value);
        Assert.Equal("Europe/Prague", read.TimeZone);
        await using var ctx = factory.CreateContext();
        Assert.Equal("Europe/Bratislava", (await ctx.Events.AsNoTracking().SingleAsync(e => e.Id == original.Id)).TimeZone);
        var changed = Assert.IsType<EventData>(Assert.IsType<OkObjectResult>(await controller.UpdateEvent(original.StoreId, original.Id, new() { Title = "Synthetic title only" })).Value);
        Assert.Equal("Europe/Prague", changed.TimeZone);
        var actual = await ctx.Events.AsNoTracking().SingleAsync(e => e.Id == original.Id);
        Assert.Equal("Europe/Prague", actual.TimeZone); Assert.Equal("Synthetic title only", actual.Title);
        Assert.Equal(original.StartDate.Ticks, actual.StartDate.Ticks); Assert.Equal(original.EndDate!.Value.Ticks, actual.EndDate!.Value.Ticks);
        Assert.Equal(original.ReminderSentAt, actual.ReminderSentAt);
    }

    [OrderDatabaseFact]
    public async Task Zone_only_partial_API_update_preserves_microsecond_instants_reminders_and_other_metadata()
    {
        var (factory, original) = await Seed(); var controller = Controller(factory, original.StoreId);
        var response = Assert.IsType<OkObjectResult>(await controller.UpdateEvent(original.StoreId, original.Id, new() { TimeZone = "America/New_York" }));
        Assert.Equal("America/New_York", Assert.IsType<EventData>(response.Value).TimeZone);
        await using var ctx = factory.CreateContext(); var actual = await ctx.Events.AsNoTracking().SingleAsync(e => e.Id == original.Id);
        Assert.Equal(original.StartDate.Ticks, actual.StartDate.Ticks); Assert.Equal(original.EndDate!.Value.Ticks, actual.EndDate!.Value.Ticks);
        Assert.Equal(original.ReminderSentAt, actual.ReminderSentAt); Assert.Equal(original.Title, actual.Title); Assert.Equal(original.Currency, actual.Currency);
    }

    [OrderDatabaseFact]
    public async Task API_omitted_end_and_null_zone_preserve_but_explicit_null_end_clears_and_foreign_store_cannot_mutate()
    {
        var (factory, original) = await Seed(); var controller = Controller(factory, original.StoreId);
        Assert.IsType<OkObjectResult>(await controller.UpdateEvent(original.StoreId, original.Id, new() { Title = "Synthetic changed", TimeZone = null! }));
        await using var ctx = factory.CreateContext(); var unchanged = await ctx.Events.AsNoTracking().SingleAsync(e => e.Id == original.Id);
        Assert.Equal(original.EndDate, unchanged.EndDate); Assert.Equal(original.TimeZone, unchanged.TimeZone);
        Assert.Equal(404, Assert.IsType<ObjectResult>(await controller.UpdateEvent("foreign-store", original.Id, new() { TimeZone = "UTC" })).StatusCode);
        Assert.IsType<OkObjectResult>(await controller.UpdateEvent(original.StoreId, original.Id, new() { EndLocal = null! }));
        Assert.Null((await ctx.Events.AsNoTracking().SingleAsync(e => e.Id == original.Id)).EndDate);
    }

    [OrderDatabaseFact]
    public async Task Civil_API_create_converts_once_while_future_legacy_UTC_create_retains_null_zone()
    {
        var (factory, original) = await Seed(); var controller = Controller(factory, original.StoreId);
        var civil = Assert.IsType<EventData>(Assert.IsType<CreatedAtActionResult>(await controller.CreateEvent(original.StoreId,
            new() { Title = "Synthetic civil", EventType = "Physical", Currency = "EUR", TimeZone = "Europe/Prague", StartLocal = "2035-11-28T09:00:00" })).Value);
        Assert.Equal(8, civil.StartDate.Hour); Assert.Equal("Europe/Prague", civil.TimeZone);
        var legacy = Assert.IsType<EventData>(Assert.IsType<CreatedAtActionResult>(await controller.CreateEvent(original.StoreId,
            new() { Title = "Synthetic legacy", EventType = "Physical", Currency = "EUR", StartDate = new DateTime(2035, 11, 28, 8, 0, 0, DateTimeKind.Utc) })).Value);
        Assert.Null(legacy.TimeZone); Assert.Equal(8, legacy.StartDate.Hour);
    }

    [OrderDatabaseFact]
    public async Task Invalid_civil_API_dates_have_stable_422_codes_and_leave_the_event_unchanged()
    {
        var (factory, original) = await Seed(); var controller = Controller(factory, original.StoreId);
        foreach (var (civil, code) in new[] { ("2027-03-28T02:30:00", "event-start-local-nonexistent"), ("2027-10-31T02:30:00", "event-start-local-ambiguous") })
        {
            var result = Assert.IsType<ObjectResult>(await controller.UpdateEvent(original.StoreId, original.Id, new() { TimeZone = "Europe/Prague", StartLocal = civil }));
            Assert.Equal(422, result.StatusCode); Assert.Equal(code, Assert.IsType<GreenfieldAPIError>(result.Value).Code);
        }
        await using var ctx = factory.CreateContext(); var actual = await ctx.Events.AsNoTracking().SingleAsync(e => e.Id == original.Id);
        Assert.Equal(original.StartDate, actual.StartDate); Assert.Equal(original.EndDate, actual.EndDate);
    }

    [OrderDatabaseFact]
    public async Task Stale_native_zone_only_explicit_date_and_end_only_forms_cannot_restore_an_old_schedule()
    {
        foreach (var scenario in new[] { "zone-only", "explicit-start", "end-only", "zone-metadata-only", "type-metadata-only" })
        {
            var (factory, original) = await Seed();
            var vm = new UpdateSimpleTicketSalesEventViewModel { EventId = original.Id, StoreId = original.StoreId,
                EventType = EventType.Physical, TimeZone = "America/New_York", Title = original.Title, Currency = original.Currency,
                OriginalTimeZone = original.TimeZone, OriginalEventType = original.EventType,
                OriginalStartUtc = original.StartDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                OriginalEndUtc = original.EndDate!.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                StartLocal = EventTimeZoneService.Civil(original.StartDate, original.TimeZone),
                EndLocal = EventTimeZoneService.Civil(original.EndDate.Value, original.TimeZone) };
            if (scenario == "zone-metadata-only") vm.TimeZone = "UTC";
            if (scenario == "explicit-start") { vm.StartEdited = true; vm.StartLocal = "2035-11-28T11:00:00"; }
            await using (var writer = factory.CreateContext())
            {
                var newer = await writer.Events.SingleAsync(e => e.Id == original.Id);
                if (scenario == "end-only") newer.EndDate = newer.EndDate!.Value.AddHours(2);
                else if (scenario == "zone-metadata-only") newer.TimeZone = "America/New_York";
                else if (scenario == "type-metadata-only") newer.EventType = EventType.Virtual;
                else newer.StartDate = newer.StartDate.AddHours(2);
                await writer.SaveChangesAsync();
            }
            var context = new DefaultHttpContext(); context.SetStoreData(new BTCPayServer.Data.StoreData { Id = original.StoreId });
            var services = new ServiceCollection().BuildServiceProvider();
            var controller = new UITicketSalesController(null!, null!, null!, null!, null!, null!, factory, null!,
                new RaffleEventBundleClientProvider(services), new RaffleListClientProvider(services))
            { ControllerContext = new ControllerContext { HttpContext = context }, TempData = new TempDataDictionary(context, new EmptyTempData()) };
            var view = Assert.IsType<ViewResult>(await controller.UpdateEvent(original.StoreId, original.Id, vm));
            Assert.Same(vm, view.Model); Assert.Equal(scenario == "zone-metadata-only" ? "UTC" : "America/New_York", vm.TimeZone);
            Assert.NotEmpty(controller.ModelState[scenario == "end-only" ? nameof(vm.EndLocal) : nameof(vm.StartLocal)]!.Errors);
            await using var verify = factory.CreateContext(); var persisted = await verify.Events.AsNoTracking().SingleAsync(e => e.Id == original.Id);
            Assert.Equal(scenario is "end-only" or "zone-metadata-only" or "type-metadata-only" ? original.StartDate : original.StartDate.AddHours(2), persisted.StartDate);
            Assert.Equal(scenario == "end-only" ? original.EndDate.Value.AddHours(2) : original.EndDate, persisted.EndDate);
            Assert.Equal(scenario == "zone-metadata-only" ? "America/New_York" : original.TimeZone, persisted.TimeZone);
            Assert.Equal(scenario == "type-metadata-only" ? EventType.Virtual : original.EventType, persisted.EventType);
            Assert.Equal(original.ReminderSentAt, persisted.ReminderSentAt);
        }
    }
    private sealed class EmptyTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [OrderDatabaseFact]
    public async Task Actual_migration_backfills_only_physical_metadata_without_changing_UTC_timestamps_or_other_records()
    {
        var factory = Factory(); await using var ctx = factory.CreateContext(); var schema = "zone_migration_" + Guid.NewGuid().ToString("N");
#pragma warning disable EF1002
        await ctx.Database.ExecuteSqlRawAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            await ctx.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE "{schema}"."Events" ("Id" text PRIMARY KEY, "EventType" integer, "StartDate" timestamp with time zone, "EndDate" timestamp with time zone, "ReminderSentAt" timestamp with time zone);
                CREATE TABLE "{schema}"."Orders" ("Id" text PRIMARY KEY, "Amount" integer);
                INSERT INTO "{schema}"."Events" VALUES
                    ('physical',1,'2026-11-28 08:00:00.123456+00','2026-11-29 11:00:00.654321+00','2026-10-01 01:00:00+00'),
                    ('virtual',0,'2026-11-28 08:00:00.123456+00',NULL,NULL);
                INSERT INTO "{schema}"."Orders" VALUES ('synthetic',12345);
                """);
            var migration = new BTCPayServer.Plugins.SatfluxTickets.Data.Migrations.EventVenueTimeZone();
            foreach (var command in ctx.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations))
                await ctx.Database.ExecuteSqlRawAsync(command.CommandText.Replace("BTCPayServer.Plugins.SatfluxTickets", schema, StringComparison.Ordinal));
            await using var connection = new NpgsqlConnection(ctx.Database.GetConnectionString()); await connection.OpenAsync();
            await using var query = new NpgsqlCommand($"SELECT \"Id\", \"TimeZone\", \"StartDate\", \"EndDate\", \"ReminderSentAt\" FROM \"{schema}\".\"Events\" ORDER BY \"Id\"", connection);
            await using (var reader = await query.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync()); Assert.Equal("physical", reader.GetString(0)); Assert.Equal("Europe/Bratislava", reader.GetString(1));
                Assert.Equal(new DateTime(2026,11,28,8,0,0,DateTimeKind.Utc).AddTicks(1234560), reader.GetDateTime(2));
                Assert.Equal(new DateTime(2026,11,29,11,0,0,DateTimeKind.Utc).AddTicks(6543210), reader.GetDateTime(3));
                Assert.Equal(new DateTime(2026,10,1,1,0,0,DateTimeKind.Utc), reader.GetDateTime(4));
                Assert.True(await reader.ReadAsync()); Assert.Equal("virtual", reader.GetString(0)); Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(3));
                Assert.Equal(new DateTime(2026,11,28,8,0,0,DateTimeKind.Utc).AddTicks(1234560), reader.GetDateTime(2)); Assert.False(await reader.ReadAsync());
            }
            await using var unchanged = new NpgsqlCommand($"SELECT \"Amount\" FROM \"{schema}\".\"Orders\" WHERE \"Id\"='synthetic'", connection);
            Assert.Equal(12345, await unchanged.ExecuteScalarAsync());
            // A new installation runs the immutable backfill and then the additive
            // normalization. Existing 2.2.1 installations start at this second step.
            await ctx.Database.ExecuteSqlRawAsync($"""
                INSERT INTO "{schema}"."Events" ("Id", "EventType", "TimeZone", "StartDate") VALUES
                    ('already-native',1,'Europe/Prague','2026-11-28 08:00:00.123456+00'),
                    ('other-native',1,'America/New_York','2026-11-28 08:00:00.123456+00'),
                    ('unsupported',1,'Europe/Unknown','2026-11-28 08:00:00.123456+00'),
                    ('legacy-null',1,NULL,'2026-11-28 08:00:00.123456+00');
                CREATE TABLE "{schema}"."Tickets" ("Id" text PRIMARY KEY, "EventId" text REFERENCES "{schema}"."Events"("Id"));
                INSERT INTO "{schema}"."Tickets" VALUES ('synthetic-ticket','physical');
                """);
            var normalize = new BTCPayServer.Plugins.SatfluxTickets.Data.Migrations.NormalizeLegacyEventTimeZone();
            Assert.Empty(normalize.DownOperations); // Never reverse unrelated canonical Prague metadata.
            foreach (var command in ctx.GetService<IMigrationsSqlGenerator>().Generate(normalize.UpOperations))
                await ctx.Database.ExecuteSqlRawAsync(command.CommandText.Replace("BTCPayServer.Plugins.SatfluxTickets", schema, StringComparison.Ordinal));
            await using var normalized = new NpgsqlCommand($"""
                SELECT "TimeZone", "StartDate", "EndDate", "ReminderSentAt",
                    (SELECT "Amount" FROM "{schema}"."Orders" WHERE "Id"='synthetic'),
                    (SELECT "EventId" FROM "{schema}"."Tickets" WHERE "Id"='synthetic-ticket'),
                    (SELECT "TimeZone" FROM "{schema}"."Events" WHERE "Id"='already-native'),
                    (SELECT "TimeZone" FROM "{schema}"."Events" WHERE "Id"='other-native'),
                    (SELECT "TimeZone" FROM "{schema}"."Events" WHERE "Id"='virtual'),
                    (SELECT "TimeZone" FROM "{schema}"."Events" WHERE "Id"='unsupported'),
                    (SELECT "TimeZone" FROM "{schema}"."Events" WHERE "Id"='legacy-null')
                FROM "{schema}"."Events" WHERE "Id"='physical'
                """, connection);
            await using var final = await normalized.ExecuteReaderAsync(); Assert.True(await final.ReadAsync());
            Assert.Equal("Europe/Prague", final.GetString(0));
            Assert.Equal(new DateTime(2026,11,28,8,0,0,DateTimeKind.Utc).AddTicks(1234560), final.GetDateTime(1));
            Assert.Equal(new DateTime(2026,11,29,11,0,0,DateTimeKind.Utc).AddTicks(6543210), final.GetDateTime(2));
            Assert.Equal(new DateTime(2026,10,1,1,0,0,DateTimeKind.Utc), final.GetDateTime(3));
            Assert.Equal(12345, final.GetInt32(4)); Assert.Equal("physical", final.GetString(5));
            Assert.Equal("Europe/Prague", final.GetString(6)); Assert.Equal("America/New_York", final.GetString(7)); Assert.True(final.IsDBNull(8));
            Assert.Equal("Europe/Unknown", final.GetString(9)); Assert.True(final.IsDBNull(10));
        }
        finally { await ctx.Database.ExecuteSqlRawAsync($"DROP SCHEMA \"{schema}\" CASCADE"); }
#pragma warning restore EF1002
    }
}
