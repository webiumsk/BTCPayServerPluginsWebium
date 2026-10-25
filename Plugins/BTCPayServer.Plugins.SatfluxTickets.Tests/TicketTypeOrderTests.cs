using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.SatfluxTickets.Controllers;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using EntityState = BTCPayServer.Plugins.SatfluxTickets.Data.EntityState;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class TicketTypeOrderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(9007199254740992L)]
    public void Missing_negative_or_unsafe_revision_is_rejected(long? revision) =>
        Assert.False(TicketTypeOrderService.IsValidRequest(new() { ExpectedRevision = revision, TicketTypeIds = ["a"] }));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_ids_are_rejected(string id) => Assert.False(TicketTypeOrderService.IsValidRequest(
        new() { ExpectedRevision = 0, TicketTypeIds = [id] }));

    [Fact]
    public void Oversized_ids_arrays_and_duplicates_are_rejected()
    {
        foreach (var ids in new[] { new[] { new string('a', 129) }, new[] { "same", "same" }, Enumerable.Range(0, 1001).Select(i => i.ToString()).ToArray() })
            Assert.False(TicketTypeOrderService.IsValidRequest(new() { ExpectedRevision = 0, TicketTypeIds = ids }));
        Assert.False(TicketTypeOrderService.IsValidRequest(new() { ExpectedRevision = 0, TicketTypeIds = null! }));
        Assert.False(TicketTypeOrderService.IsValidRequest(null!));
    }

    [Fact]
    public void Empty_order_is_valid_only_for_an_empty_membership()
    {
        Assert.True(TicketTypeOrderService.IsValidRequest(new() { ExpectedRevision = 0, TicketTypeIds = [] }));
        Assert.True(TicketTypeOrderService.IsCompletePermutation([], []));
        Assert.False(TicketTypeOrderService.IsCompletePermutation([], ["a"]));
    }

    [Fact]
    public void Permutation_is_case_sensitive_complete_and_unique()
    {
        Assert.True(TicketTypeOrderService.IsCompletePermutation(["b", "a"], ["a", "b"]));
        Assert.False(TicketTypeOrderService.IsCompletePermutation(["a", "a"], ["a", "b"]));
        Assert.False(TicketTypeOrderService.IsCompletePermutation(["a", "foreign"], ["a", "b"]));
        Assert.False(TicketTypeOrderService.IsCompletePermutation(["A", "b"], ["a", "b"]));
    }

    [Fact]
    public void Display_order_keeps_disabled_types_and_stable_id_ties()
    {
        var result = TicketTypeOrderService.InDisplayOrder(new[]
        {
            new TicketType { Id = "z", DisplayOrder = 0, TicketTypeState = EntityState.Disabled },
            new TicketType { Id = "b", DisplayOrder = 1 },
            new TicketType { Id = "a", DisplayOrder = 1 }
        }.AsQueryable()).Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "z", "a", "b" }, result);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("put")]
    public async Task Foreign_store_is_rejected_before_any_database_access(string operation)
    {
        var controller = new GreenfieldSatfluxTicketTypesController(null!, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.SetStoreData(new StoreData { Id = "owned-store" });
        var result = operation == "get"
            ? await controller.GetTicketTypeOrder("foreign-store", "event")
            : await controller.SetTicketTypeOrder("foreign-store", "event", new() { ExpectedRevision = 0, TicketTypeIds = [] });
        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void Native_ordering_translation_keys_and_placeholders_match_all_five_locales()
    {
        var english = TicketTypeOrderText.GetAll("en");
        foreach (var locale in new[] { "sk", "es", "de", "cs" })
        {
            var translated = TicketTypeOrderText.GetAll(locale);
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            foreach (var key in english.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[key]));
                Assert.Equal(english[key].Contains("{0}"), translated[key].Contains("{0}"));
            }
        }
        Assert.Equal(english, TicketTypeOrderText.GetAll("fr"));
    }

    [Fact]
    public void Ordering_migration_is_discoverable_and_snapshot_matches_runtime_model()
    {
        var factory = new SimpleTicketSalesDbContextFactory(Options.Create(new DatabaseOptions
        { ConnectionString = "Host=127.0.0.1;Database=satflux_tickets_ordering_metadata_only;Username=test" }));
        using var ctx = factory.CreateContext();
        Assert.Contains("20261021120000_TicketTypeDisplayOrder", ctx.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.False(ctx.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Ordering_requires_store_modify_authorization_and_native_csrf()
    {
        var apiAuth = Assert.Single(typeof(GreenfieldSatfluxTicketTypesController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(BTCPayServer.Client.Policies.CanModifyStoreSettings, apiAuth.Policy);
        Assert.Equal(BTCPayServer.Abstractions.Constants.AuthenticationSchemes.Greenfield, apiAuth.AuthenticationSchemes);
        Assert.NotEmpty(typeof(UITicketTypeController).GetCustomAttributes(typeof(AutoValidateAntiforgeryTokenAttribute), true));
        var nativeAuth = Assert.Single(typeof(UITicketTypeController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(BTCPayServer.Abstractions.Constants.AuthenticationSchemes.Cookie, nativeAuth.AuthenticationSchemes);
    }
}

/// <summary>Requires an explicitly supplied, disposable, local database; never uses application settings.</summary>
public sealed class OrderDatabaseFactAttribute : FactAttribute
{
    public OrderDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TICKETS_ORDER_TEST_CONNECTION")))
            Skip = "TICKETS_ORDER_TEST_CONNECTION must name a disposable local satflux_tickets_ordering_* database.";
    }
}

[Collection("Tickets disposable PostgreSQL")]
public class TicketTypeOrderDatabaseTests
{
    private static SimpleTicketSalesDbContextFactory Factory()
    {
        var connection = Environment.GetEnvironmentVariable("TICKETS_ORDER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection));
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        Assert.Contains(parsed.Host, new[] { "localhost", "127.0.0.1", "::1" });
        Assert.StartsWith("satflux_tickets_ordering_", parsed.Database);
        return new SimpleTicketSalesDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connection! }));
    }

    private static async Task<(SimpleTicketSalesDbContextFactory Factory, Event Event, string[] Ids)> Seed()
    {
        var factory = Factory();
        await using var ctx = factory.CreateContext();
        await ctx.Database.MigrateAsync();
        var eventId = Guid.NewGuid().ToString();
        var ticketEvent = new Event { Id = eventId, StoreId = "test-owner", Title = "Disposable ordering test", Currency = "EUR", StartDate = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow };
        var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        ctx.Events.Add(ticketEvent);
        for (var i = 0; i < ids.Length; i++) ctx.TicketTypes.Add(new TicketType
        {
            Id = ids[i], EventId = eventId, DisplayOrder = i, Name = "Type " + i, Price = 12.34m,
            Quantity = 100, QuantitySold = 7, IsDefault = i == 0,
            TicketTypeState = i == 1 ? EntityState.Disabled : EntityState.Active
        });
        await ctx.SaveChangesAsync();
        return (factory, ticketEvent, ids);
    }

    [OrderDatabaseFact]
    public async Task Reorder_persists_all_members_and_preserves_sales_prices_defaults_and_states()
    {
        var (factory, ticketEvent, ids) = await Seed();
        var service = new TicketTypeOrderService(factory);
        Assert.Null(await service.GetAsync("foreign-owner", ticketEvent.Id));
        Assert.Null(await service.SetAsync("foreign-owner", ticketEvent.Id, new() { TicketTypeIds = ids, ExpectedRevision = 0 }));
        var reversed = ids.Reverse().ToArray();
        var result = await service.SetAsync(ticketEvent.StoreId, ticketEvent.Id, new() { TicketTypeIds = reversed, ExpectedRevision = 0 });
        Assert.Equal(1, result.Revision);
        Assert.Equal(reversed, (await service.GetAsync(ticketEvent.StoreId, ticketEvent.Id)).TicketTypeIds);
        await using var ctx = factory.CreateContext();
        var types = await ctx.TicketTypes.Where(t => t.EventId == ticketEvent.Id).ToListAsync();
        Assert.All(types, t => { Assert.Equal(7, t.QuantitySold); Assert.Equal(100, t.Quantity); Assert.Equal(12.34m, t.Price); });
        Assert.Equal(ids[0], Assert.Single(types, t => t.IsDefault).Id);
        Assert.Equal(EntityState.Disabled, types.Single(t => t.Id == ids[1]).TicketTypeState);
    }

    [OrderDatabaseFact]
    public async Task Stale_foreign_and_missing_memberships_fail_atomically()
    {
        var (factory, ticketEvent, ids) = await Seed();
        var service = new TicketTypeOrderService(factory);
        foreach (var request in new[]
        {
            new TicketTypeOrderRequest { TicketTypeIds = ids.Reverse().ToArray(), ExpectedRevision = 1 },
            new TicketTypeOrderRequest { TicketTypeIds = [ids[0], ids[1], "foreign-id"], ExpectedRevision = 0 },
            new TicketTypeOrderRequest { TicketTypeIds = ids[..2], ExpectedRevision = 0 }
        }) await Assert.ThrowsAsync<TicketTypeOrderConflictException>(() => service.SetAsync(ticketEvent.StoreId, ticketEvent.Id, request));
        var snapshot = await service.GetAsync(ticketEvent.StoreId, ticketEvent.Id);
        Assert.Equal(0, snapshot.Revision);
        Assert.Equal(ids, snapshot.TicketTypeIds);
    }

    [OrderDatabaseFact]
    public async Task Concurrent_reorders_with_same_revision_have_exactly_one_winner()
    {
        var (factory, ticketEvent, ids) = await Seed();
        var service = new TicketTypeOrderService(factory);
        async Task<bool> Attempt(string[] order)
        {
            try { await service.SetAsync(ticketEvent.StoreId, ticketEvent.Id, new() { TicketTypeIds = order, ExpectedRevision = 0 }); return true; }
            catch (TicketTypeOrderConflictException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(ids.Reverse().ToArray()), Attempt([ids[1], ids[0], ids[2]]));
        Assert.Single(results, result => result);
        Assert.Equal(1, (await service.GetAsync(ticketEvent.StoreId, ticketEvent.Id)).Revision);
    }

    [OrderDatabaseFact]
    public async Task Api_creation_appends_and_deletion_invalidates_stale_order()
    {
        var (factory, ticketEvent, ids) = await Seed();
        var service = new TicketTypeOrderService(factory);
        var controller = new GreenfieldSatfluxTicketTypesController(factory, service)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.SetStoreData(new StoreData { Id = ticketEvent.StoreId });
        var created = Assert.IsType<CreatedAtActionResult>(await controller.CreateTicketType(ticketEvent.StoreId, ticketEvent.Id,
            new() { Name = "Appended", Price = 5, Quantity = 20 }));
        var addedId = Assert.IsType<TicketTypeData>(created.Value).Id;
        var appended = await service.GetAsync(ticketEvent.StoreId, ticketEvent.Id);
        Assert.Equal(ids.Concat([addedId]), appended.TicketTypeIds);
        Assert.Equal(1, appended.Revision);
        Assert.IsType<OkResult>(await controller.DeleteTicketType(ticketEvent.StoreId, ticketEvent.Id, addedId));
        await Assert.ThrowsAsync<TicketTypeOrderConflictException>(() => service.SetAsync(ticketEvent.StoreId, ticketEvent.Id,
            new() { TicketTypeIds = appended.TicketTypeIds, ExpectedRevision = appended.Revision }));
        Assert.Equal(2, (await service.GetAsync(ticketEvent.StoreId, ticketEvent.Id)).Revision);
    }
    [OrderDatabaseFact]
    public async Task Concurrent_append_and_reorder_produce_complete_current_membership()
    {
        var (factory, ticketEvent, ids) = await Seed();
        var service = new TicketTypeOrderService(factory);
        var controller = new GreenfieldSatfluxTicketTypesController(factory, service)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.SetStoreData(new StoreData { Id = ticketEvent.StoreId });
        async Task Reorder()
        {
            try { await service.SetAsync(ticketEvent.StoreId, ticketEvent.Id, new() { TicketTypeIds = ids.Reverse().ToArray(), ExpectedRevision = 0 }); }
            catch (TicketTypeOrderConflictException) { /* Membership won the row lock; reload is required. */ }
        }
        var create = controller.CreateTicketType(ticketEvent.StoreId, ticketEvent.Id, new() { Name = "Concurrent", Price = 2, Quantity = 3 });
        await Task.WhenAll(create, Reorder());
        var addedId = Assert.IsType<TicketTypeData>(Assert.IsType<CreatedAtActionResult>(await create).Value).Id;
        var current = await service.GetAsync(ticketEvent.StoreId, ticketEvent.Id);
        Assert.Equal(4, current.TicketTypeIds.Length);
        Assert.Equal(addedId, current.TicketTypeIds[^1]);
        Assert.True(current.Revision is 1 or 2);
        Assert.True(TicketTypeOrderService.IsCompletePermutation(current.TicketTypeIds, ids.Concat([addedId]).ToArray()));
    }

    [OrderDatabaseFact]
    public async Task Whole_event_deletion_and_reorder_do_not_leave_orphan_membership()
    {
        var (factory, ticketEvent, ids) = await Seed();
        var service = new TicketTypeOrderService(factory);
        var controller = new GreenfieldSatfluxTicketsEventsController(null!, null!, null!, factory, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.SetStoreData(new StoreData { Id = ticketEvent.StoreId });
        var reorder = service.SetAsync(ticketEvent.StoreId, ticketEvent.Id, new() { TicketTypeIds = ids.Reverse().ToArray(), ExpectedRevision = 0 });
        var deletion = controller.DeleteEvent(ticketEvent.StoreId, ticketEvent.Id);
        await Task.WhenAll(reorder, deletion);
        Assert.IsType<OkResult>(await deletion);
        Assert.Null(await service.GetAsync(ticketEvent.StoreId, ticketEvent.Id));
        await using var ctx = factory.CreateContext();
        Assert.False(await ctx.TicketTypes.AnyAsync(t => t.EventId == ticketEvent.Id));
    }

    [OrderDatabaseFact]
    public async Task Additive_migration_backfills_each_event_by_name_then_id_without_changing_other_columns()
    {
        var factory = Factory();
        await using var ctx = factory.CreateContext();
        var schema = "order_migration_test_" + Guid.NewGuid().ToString("N");
        // Identifiers cannot be SQL parameters. This private schema is only a fixed prefix and GUID hex.
#pragma warning disable EF1002
        await ctx.Database.ExecuteSqlRawAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            await ctx.Database.ExecuteSqlRawAsync($"""
                CREATE TABLE "{schema}"."Events" ("Id" text PRIMARY KEY);
                CREATE TABLE "{schema}"."TicketTypes" ("Id" text PRIMARY KEY, "EventId" text, "Name" text, "QuantitySold" integer);
                INSERT INTO "{schema}"."Events" VALUES ('e1'), ('e2');
                INSERT INTO "{schema}"."TicketTypes" VALUES ('c','e1','Bravo',11), ('b','e1','Alpha',12), ('a','e1','Alpha',13), ('z','e2','Alpha',14);
                """);
            var migration = new BTCPayServer.Plugins.SatfluxTickets.Data.Migrations.TicketTypeDisplayOrder();
            foreach (var command in ctx.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations))
                await ctx.Database.ExecuteSqlRawAsync(command.CommandText.Replace("BTCPayServer.Plugins.SatfluxTickets", schema, StringComparison.Ordinal));
            await using var connection = new NpgsqlConnection(ctx.Database.GetConnectionString());
            await connection.OpenAsync();
            await using var commandQuery = new NpgsqlCommand($"SELECT \"Id\", \"DisplayOrder\", \"QuantitySold\" FROM \"{schema}\".\"TicketTypes\" ORDER BY \"EventId\", \"DisplayOrder\"", connection);
            await using var reader = await commandQuery.ExecuteReaderAsync();
            var actual = new List<(string, int, int)>();
            while (await reader.ReadAsync()) actual.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
            Assert.Equal(new[] { ("a", 0, 13), ("b", 1, 12), ("c", 2, 11), ("z", 0, 14) }, actual);
        }
        finally
        {
            await ctx.Database.ExecuteSqlRawAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
#pragma warning restore EF1002
    }

    [OrderDatabaseFact]
    public async Task Native_ajax_save_returns_json_success_without_redirect()
    {
        var (factory, ticketEvent, ids) = await Seed();
        var controller = new UITicketTypeController(factory, new TicketTypeOrderService(factory))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.SetStoreData(new StoreData { Id = ticketEvent.StoreId });
        controller.Request.Headers.Accept = "application/json";
        var result = Assert.IsType<OkObjectResult>(await controller.SaveTicketTypeOrder(ticketEvent.StoreId, ticketEvent.Id,
            new() { TicketTypeIds = ids.Reverse().ToArray(), ExpectedRevision = 0 }));
        var saved = Assert.IsType<TicketTypeOrderSnapshot>(result.Value);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(ids.Reverse(), saved.TicketTypeIds);
    }

}
