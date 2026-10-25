using System.Reflection;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Npgsql;
using NpgsqlTypes;
using Newtonsoft.Json;
using Xunit;
using TransactionStatus = BTCPayServer.Plugins.SatfluxTickets.Data.TransactionStatus;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

/// <summary>Tests use only the explicitly supplied disposable localhost database; no core migrations or real invoices.</summary>
[Collection("Tickets disposable PostgreSQL")]
public class TicketSalesDatabaseTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public required SimpleTicketSalesDbContextFactory Factory { get; init; }
        public required SimpleTicketSalesHostedService Hosted { get; init; }
        public required Order Order { get; init; }
        public required TicketType Type { get; init; }
        public required InvoiceEntity Invoice { get; init; }
        public required NpgsqlConnection Connection { get; init; }
        public required string LogSchema { get; init; }

        public static async Task<Fixture> Create()
        {
            var connection = Environment.GetEnvironmentVariable("TICKETS_ORDER_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(connection));
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            Assert.Contains(parsed.Host, new[] { "localhost", "127.0.0.1", "::1" });
            Assert.StartsWith("satflux_tickets_ordering_", parsed.Database);
            var options = Options.Create(new DatabaseOptions { ConnectionString = connection! });
            var factory = new SimpleTicketSalesDbContextFactory(options);
            await using var ctx = factory.CreateContext();
            // The ordering suite/provider has migrated this same disposable DB; Migrate is idempotent.
            await ctx.Database.MigrateAsync();
            var suffix = Guid.NewGuid().ToString("N");
            var type = new TicketType { Id = "sales-type-" + suffix, EventId = "missing-event-" + suffix, Name = "Synthetic", Price = 12.34m, Quantity = 10, QuantitySold = 0, TicketTypeState = Data.EntityState.Active };
            var order = new Order
            {
                Id = "sales-order-" + suffix, StoreId = "sales-store-" + suffix, EventId = type.EventId,
                TxnId = "sales-nonce-" + suffix, Currency = "EUR", TotalAmount = 12.34m,
                PaymentStatus = TransactionStatus.New.ToString(), CreatedAt = DateTimeOffset.UtcNow
            };
            order.Tickets.Add(new Ticket
            {
                Id = "sales-ticket-" + suffix, StoreId = order.StoreId, EventId = order.EventId,
                TicketTypeId = type.Id, TicketTypeName = type.Name, Amount = type.Price,
                FirstName = "Synthetic", LastName = "Test", Email = "synthetic@example.test", QRCodeLink = "https://example.test/ticket",
                CreatedAt = DateTimeOffset.UtcNow, PaymentStatus = TransactionStatus.New.ToString()
            });
            ctx.TicketTypes.Add(type); ctx.Orders.Add(order); await ctx.SaveChangesAsync();
            var invoice = new InvoiceEntity
            {
                Version = InvoiceEntity.Lastest_Version, Id = "sales-invoice-" + suffix, InvoiceTime = DateTimeOffset.UtcNow, StoreId = order.StoreId, Price = order.TotalAmount, Currency = "EUR", Status = InvoiceStatus.Settled,
                Metadata = new InvoiceMetadata { AdditionalData = new Dictionary<string, JToken> { ["orderId"] = order.Id, ["TxnId"] = order.TxnId } },
                InternalTags = new() { SimpleTicketSalesHostedService.TICKET_SALES_PREFIX + order.TxnId }
            };
            // The production handler's existing audit log sink uses its real InvoiceRepository.
            // Only its one log table is required; isolate it in a unique temporary test schema.
            var schema = "ticket_sales_logs_" + suffix;
            var conn = new NpgsqlConnection(connection); await conn.OpenAsync();
            await using (var command = new NpgsqlCommand($"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE "{schema}"."InvoiceEvents" ("InvoiceDataId" text, "Severity" integer, "Message" text, "Timestamp" timestamp with time zone);
                CREATE TABLE "{schema}"."Invoices" ("Id" text PRIMARY KEY, "Currency" text, "Amount" numeric, "StoreDataId" text,
                    "Created" timestamp with time zone, "Blob" bytea, "Blob2" jsonb, "Status" text, "ExceptionStatus" text, "Archived" boolean NOT NULL DEFAULT false);
                CREATE TABLE "{schema}"."Payments" ("Id" text, "PaymentMethodId" text, "InvoiceDataId" text, "Currency" text, "Amount" numeric,
                    "Created" timestamp with time zone, "Blob" bytea, "Blob2" jsonb, "Accounted" boolean, "Status" text);
                """, conn)) await command.ExecuteNonQueryAsync();
            parsed.SearchPath = schema;
            var logFactory = new ApplicationDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = parsed.ConnectionString }), NullLoggerFactory.Instance);
            var logs = new Logs();
            var aggregator = new EventAggregator(logs);
            var repository = new InvoiceRepository(logFactory, aggregator);
            var hosted = new SimpleTicketSalesHostedService(null!, aggregator, repository, factory, logs, null!, null!);
            return new Fixture { Factory = factory, Hosted = hosted, Order = order, Type = type, Invoice = invoice, Connection = conn, LogSchema = schema };
        }

        public async Task PersistInvoice()
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO "{LogSchema}"."Invoices" ("Id", "Currency", "Amount", "StoreDataId", "Created", "Blob2", "Status", "ExceptionStatus")
                VALUES (@id, @currency, @amount, @store, @created, @blob, @status, 'None')
                ON CONFLICT ("Id") DO UPDATE SET "Currency" = @currency, "Amount" = @amount, "StoreDataId" = @store, "Blob2" = @blob, "Status" = @status
                """, Connection);
            command.Parameters.AddWithValue("id", Invoice.Id);
            command.Parameters.AddWithValue("currency", Invoice.Currency);
            command.Parameters.AddWithValue("amount", Invoice.Price);
            command.Parameters.AddWithValue("store", Invoice.StoreId);
            command.Parameters.AddWithValue("created", Invoice.InvoiceTime);
            command.Parameters.AddWithValue("blob", NpgsqlDbType.Jsonb, JsonConvert.SerializeObject(Invoice, InvoiceDataExtensions.DefaultSerializerSettings));
            command.Parameters.AddWithValue("status", Invoice.Status.ToString());
            await command.ExecuteNonQueryAsync();
        }

        public async Task Process(InvoiceEntity? invoice = null, bool persistCurrent = true)
        {
            if (persistCurrent) await PersistInvoice();
            var method = typeof(SimpleTicketSalesHostedService).GetMethod("RegisterTicketTransaction", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(Hosted, new object[] { invoice ?? Invoice, Order.TxnId, true, true })!;
        }

        public async ValueTask DisposeAsync()
        {
            await using var ctx = Factory.CreateContext();
            await ctx.Tickets.Where(t => t.StoreId == Order.StoreId && t.EventId == Order.EventId).ExecuteDeleteAsync();
            await ctx.Orders.Where(o => o.Id == Order.Id).ExecuteDeleteAsync();
            await ctx.TicketTypes.Where(t => t.Id == Type.Id).ExecuteDeleteAsync();
            await ctx.Events.Where(e => e.Id == Order.EventId && e.StoreId == Order.StoreId).ExecuteDeleteAsync();
            await using (var command = new NpgsqlCommand($"DROP SCHEMA \"{LogSchema}\" CASCADE", Connection)) await command.ExecuteNonQueryAsync();
            await Connection.DisposeAsync();
        }
    }

    [OrderDatabaseFact]
    public async Task Sold_out_event_remains_visible_but_new_purchase_and_invalid_event_scopes_stay_denied()
    {
        await using var fixture = await Fixture.Create();
        await using var ctx = fixture.Factory.CreateContext();
        var ev = new Event
        {
            Id = fixture.Order.EventId, StoreId = fixture.Order.StoreId, Title = "Synthetic sold-out event",
            HasMaximumCapacity = true, MaximumEventCapacity = 1, EventState = Data.EntityState.Active,
            StartDate = DateTime.UtcNow.AddDays(1), CreatedAt = DateTimeOffset.UtcNow
        };
        ctx.Events.Add(ev);
        var order = await ctx.Orders.SingleAsync(o => o.Id == fixture.Order.Id);
        order.PaymentStatus = TransactionStatus.Settled.ToString(); await ctx.SaveChangesAsync();
        var constructor = typeof(UITicketSalesPublicController).GetConstructors().Single();
        var controller = (UITicketSalesPublicController)constructor.Invoke(new object?[constructor.GetParameters().Length]);
        var validate = typeof(UITicketSalesPublicController).GetMethod("ValidateEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool Valid(string store, bool checkCapacity) => (bool)validate.Invoke(controller, new object[] { ctx, store, ev.Id, checkCapacity })!;
        Assert.True(Valid(ev.StoreId, false));
        Assert.False(Valid(ev.StoreId, true));
        Assert.False(Valid("foreign-store", false));
        ev.EventState = Data.EntityState.Disabled; await ctx.SaveChangesAsync();
        Assert.False(Valid(ev.StoreId, false));
        ev.EventState = Data.EntityState.Active; ev.StartDate = DateTime.UtcNow.AddDays(-2); await ctx.SaveChangesAsync();
        Assert.False(Valid(ev.StoreId, false));
    }

    [OrderDatabaseFact]
    public async Task Settled_callback_binds_missing_invoice_id_and_processes_once_across_concurrent_retries()
    {
        await using var fixture = await Fixture.Create();
        await fixture.PersistInvoice();
        await Task.WhenAll(fixture.Process(persistCurrent: false), fixture.Process(persistCurrent: false)).WaitAsync(TimeSpan.FromSeconds(15));
        await using var ctx = fixture.Factory.CreateContext();
        var order = await ctx.Orders.Include(o => o.Tickets).SingleAsync(o => o.Id == fixture.Order.Id);
        Assert.Equal(fixture.Invoice.Id, order.InvoiceId);
        Assert.Equal(TransactionStatus.Settled.ToString(), order.PaymentStatus);
        Assert.Equal(TransactionStatus.Settled.ToString(), Assert.Single(order.Tickets).PaymentStatus);
        Assert.Equal(1, (await ctx.TicketTypes.SingleAsync(t => t.Id == fixture.Type.Id)).QuantitySold);
        Assert.All(order.Tickets, t => Assert.False(string.IsNullOrEmpty(t.QRCodeLink)));
        // Missing synthetic event deliberately prevents email/raffle access. No live providers are constructed.
    }

    [OrderDatabaseFact]
    public async Task Callback_waits_for_public_checkout_guard_without_deadlocking_or_double_counting()
    {
        await using var fixture = await Fixture.Create();
        await using var publicContext = fixture.Factory.CreateContext(options => options.ExecutionStrategy(d => new NonRetryingExecutionStrategy(d)));
        var publicGuard = await TicketSalesCheckoutLock.AcquireAsync(publicContext, fixture.Order.StoreId, fixture.Order.EventId, fixture.Order.TxnId, CancellationToken.None);
        var callback = fixture.Process();
        await Task.Delay(100);
        Assert.False(callback.IsCompleted);
        await publicGuard.DisposeAsync();
        await callback.WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.Process();
        await using var ctx = fixture.Factory.CreateContext();
        Assert.Equal(1, (await ctx.TicketTypes.SingleAsync(t => t.Id == fixture.Type.Id)).QuantitySold);
    }

    [OrderDatabaseFact]
    public async Task Wrong_amount_and_an_already_different_invoice_binding_make_no_changes()
    {
        await using var fixture = await Fixture.Create();
        fixture.Invoice.Price = .01m;
        await fixture.Process();
        await using (var ctx = fixture.Factory.CreateContext())
        {
            var order = await ctx.Orders.SingleAsync(o => o.Id == fixture.Order.Id);
            Assert.Null(order.InvoiceId); Assert.Equal(TransactionStatus.New.ToString(), order.PaymentStatus);
            order.InvoiceId = "different-existing-invoice"; await ctx.SaveChangesAsync();
        }
        fixture.Invoice.Price = 12.34m;
        await fixture.Process();
        await using var verify = fixture.Factory.CreateContext();
        Assert.Equal("different-existing-invoice", (await verify.Orders.SingleAsync(o => o.Id == fixture.Order.Id)).InvoiceId);
        Assert.Equal(0, (await verify.TicketTypes.SingleAsync(t => t.Id == fixture.Type.Id)).QuantitySold);
    }

    [OrderDatabaseFact]
    public async Task Foreign_or_incomplete_recovery_identity_has_no_side_effects()
    {
        foreach (var attack in new[] { "foreign-store", "missing-metadata", "wrong-order", "wrong-nonce", "wrong-tag", "wrong-currency" })
        {
            await using var fixture = await Fixture.Create();
            if (attack == "foreign-store") fixture.Invoice.StoreId = "foreign-store";
            if (attack == "missing-metadata") fixture.Invoice.Metadata.AdditionalData.Clear();
            if (attack == "wrong-order") fixture.Invoice.Metadata.OrderId = "foreign-order";
            if (attack == "wrong-nonce") fixture.Invoice.Metadata.AdditionalData["TxnId"] = "foreign-nonce";
            if (attack == "wrong-tag") fixture.Invoice.InternalTags.Clear();
            if (attack == "wrong-currency") fixture.Invoice.Currency = "USD";
            await fixture.Process();
            await using var verify = fixture.Factory.CreateContext();
            var order = await verify.Orders.SingleAsync(o => o.Id == fixture.Order.Id);
            Assert.Null(order.InvoiceId); Assert.Equal(TransactionStatus.New.ToString(), order.PaymentStatus);
            Assert.Equal(0, (await verify.TicketTypes.SingleAsync(t => t.Id == fixture.Type.Id)).QuantitySold);
        }
    }

    [OrderDatabaseFact]
    public async Task Stale_settled_snapshot_cannot_override_current_persisted_nonterminal_state()
    {
        await using var fixture = await Fixture.Create();
        await fixture.PersistInvoice();
        var snapshot = JsonConvert.DeserializeObject<InvoiceEntity>(JsonConvert.SerializeObject(fixture.Invoice, InvoiceDataExtensions.DefaultSerializerSettings), InvoiceDataExtensions.DefaultSerializerSettings)!;
        snapshot.Id = fixture.Invoice.Id; snapshot.StoreId = fixture.Invoice.StoreId; snapshot.Price = fixture.Invoice.Price; snapshot.Currency = fixture.Invoice.Currency;
        snapshot.Status = InvoiceStatus.Settled;
        fixture.Invoice.Status = InvoiceStatus.New;
        await fixture.PersistInvoice();
        await fixture.Process(snapshot, persistCurrent: false);
        await using var verify = fixture.Factory.CreateContext();
        var order = await verify.Orders.SingleAsync(o => o.Id == fixture.Order.Id);
        Assert.Null(order.InvoiceId); Assert.Equal(TransactionStatus.New.ToString(), order.PaymentStatus);
        Assert.Equal(0, (await verify.TicketTypes.SingleAsync(t => t.Id == fixture.Type.Id)).QuantitySold);
    }

    [OrderDatabaseFact]
    public async Task One_save_rolls_back_order_and_qr_tickets_together_when_ticket_insert_fails()
    {
        await using var fixture = await Fixture.Create();
        var failedOrderId = "rollback-" + Guid.NewGuid().ToString("N");
        await using (var ctx = fixture.Factory.CreateContext())
        {
            var order = new Order { Id = failedOrderId, StoreId = fixture.Order.StoreId, EventId = fixture.Order.EventId, CreatedAt = DateTimeOffset.UtcNow };
            order.Tickets.Add(new Ticket { Id = fixture.Order.Tickets.Single().Id, QRCodeLink = "https://example.test/synthetic", CreatedAt = DateTimeOffset.UtcNow });
            ctx.Orders.Add(order);
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
        await using var verify = fixture.Factory.CreateContext();
        Assert.False(await verify.Orders.AnyAsync(o => o.Id == failedOrderId));
    }
}
