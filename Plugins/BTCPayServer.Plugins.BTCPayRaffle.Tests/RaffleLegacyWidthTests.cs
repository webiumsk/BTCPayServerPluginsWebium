using System.Globalization;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.BTCPayRaffle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

public sealed class RaffleDatabaseFactAttribute : FactAttribute
{
    public RaffleDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")))
            Skip = "Requires disposable PostgreSQL (CASHUMELT_TEST_CONNECTION); CI supplies it.";
    }
}

public class RaffleLegacyWidthTests(ITestOutputHelper output)
{
    [RaffleDatabaseFact]
    public async Task Fresh_runner_schema_has_canonical_widths_and_accepts_current_payloads()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Run();
        Assert.Equal("text:", await fixture.Shape("Raffles", "Description"));
        Assert.Equal("character varying:100", await fixture.Shape("RaffleTickets", "InvoiceId"));
        await fixture.WriteCurrentPayload();
        var snapshot = await fixture.Rows();
        await fixture.AuditWidthAlterations();
        await fixture.Run();
        await fixture.Run();
        Assert.Equal(snapshot, await fixture.Rows());
        Assert.Equal(0, await fixture.WidthAlterations());
        output.WriteLine("Inherited runner fallback warnings observed: " + fixture.Warnings);
    }

    [RaffleDatabaseFact]
    public async Task Legacy_2000_50_runner_widens_without_changing_rows_or_history_and_does_not_repeat_ddl()
    {
        await using var fixture = await Fixture.Create();
        await fixture.CreateShape("varchar(2000)", "varchar(50)");
        await fixture.SeedRows();
        var before = await fixture.Rows();
        var history = await fixture.History();
        Assert.Equal("|[]", history);
        await fixture.AuditWidthAlterations();
        await fixture.Run();
        await fixture.Run();
        Assert.Equal("text:", await fixture.Shape("Raffles", "Description"));
        Assert.Equal("character varying:100", await fixture.Shape("RaffleTickets", "InvoiceId"));
        Assert.Equal(before, await fixture.Rows());
        Assert.Equal(history, await fixture.History());
        Assert.Equal(2, await fixture.WidthAlterations());
        await fixture.WriteCurrentPayload();
        var after = await fixture.Rows();
        await fixture.Run();
        Assert.Equal(after, await fixture.Rows());
        Assert.Equal(history, await fixture.History());
        Assert.Equal(2, await fixture.WidthAlterations());
        output.WriteLine("Inherited runner fallback warnings observed: " + fixture.Warnings);
    }

    [RaffleDatabaseFact]
    public async Task Wider_columns_and_data_are_not_clamped()
    {
        await using var fixture = await Fixture.Create();
        await fixture.CreateShape("varchar(12000)", "varchar(200)");
        await fixture.SeedRows(new string('w', 10000), new string('i', 150));
        var before = await fixture.Rows();
        await fixture.AuditWidthAlterations();
        await fixture.Run();
        await fixture.Run();
        Assert.Equal("character varying:12000", await fixture.Shape("Raffles", "Description"));
        Assert.Equal("character varying:200", await fixture.Shape("RaffleTickets", "InvoiceId"));
        Assert.Equal(before, await fixture.Rows());
        Assert.Equal(0, await fixture.WidthAlterations());
    }

    [RaffleDatabaseFact]
    public async Task Unbounded_varchar_columns_remain_unbounded()
    {
        await using var fixture = await Fixture.Create();
        await fixture.CreateShape("varchar", "varchar");
        await fixture.SeedRows(new string('w', 10000), new string('i', 150));
        var before = await fixture.Rows();
        await fixture.AuditWidthAlterations();
        await fixture.Run();
        await fixture.Run();
        Assert.Equal("character varying:", await fixture.Shape("Raffles", "Description"));
        Assert.Equal("character varying:", await fixture.Shape("RaffleTickets", "InvoiceId"));
        Assert.Equal(before, await fixture.Rows());
        Assert.Equal(0, await fixture.WidthAlterations());
    }

    [RaffleDatabaseFact]
    public async Task Text_columns_remain_unbounded()
    {
        await using var fixture = await Fixture.Create();
        await fixture.CreateShape("text", "text");
        await fixture.SeedRows(new string('w', 10000), new string('i', 150));
        var before = await fixture.Rows();
        await fixture.AuditWidthAlterations();
        await fixture.Run();
        Assert.Equal("text:", await fixture.Shape("Raffles", "Description"));
        Assert.Equal("text:", await fixture.Shape("RaffleTickets", "InvoiceId"));
        Assert.Equal(before, await fixture.Rows());
        Assert.Equal(0, await fixture.WidthAlterations());
    }

    [RaffleDatabaseFact]
    public async Task Canonical_bounded_widths_are_not_unnecessarily_altered()
    {
        await using var fixture = await Fixture.Create();
        await fixture.CreateShape("varchar(8000)", "varchar(100)");
        await fixture.SeedRows();
        var before = await fixture.Rows();
        await fixture.AuditWidthAlterations();
        await fixture.Run();
        Assert.Equal("character varying:8000", await fixture.Shape("Raffles", "Description"));
        Assert.Equal("character varying:100", await fixture.Shape("RaffleTickets", "InvoiceId"));
        Assert.Equal(before, await fixture.Rows());
        Assert.Equal(0, await fixture.WidthAlterations());
        await fixture.WriteCurrentPayload();
    }

    [RaffleDatabaseFact]
    public async Task Custom_domains_and_their_constraints_are_preserved()
    {
        await using var fixture = await Fixture.Create();
        await fixture.CreateShape("varchar(2000)", "varchar(50)");
        await fixture.Sql("""
CREATE DOMAIN public.description_domain AS varchar(2000) CHECK (VALUE IS NULL OR length(VALUE) <= 2000);
CREATE DOMAIN public.invoice_domain AS varchar(50) CHECK (length(VALUE) <= 50);
ALTER TABLE "BTCPayServer.Plugins.BTCPayRaffle"."Raffles" ALTER COLUMN "Description" TYPE public.description_domain;
ALTER TABLE "BTCPayServer.Plugins.BTCPayRaffle"."RaffleTickets" ALTER COLUMN "InvoiceId" TYPE public.invoice_domain;
""");
        await fixture.SeedRows();
        var before = await fixture.Rows();
        await fixture.AuditWidthAlterations();
        await fixture.Run();
        await fixture.Run();
        Assert.Equal(before, await fixture.Rows());
        Assert.Equal("description_domain", await fixture.Scalar("SELECT domain_name FROM information_schema.columns WHERE table_schema='BTCPayServer.Plugins.BTCPayRaffle' AND table_name='Raffles' AND column_name='Description'"));
        Assert.Equal("invoice_domain", await fixture.Scalar("SELECT domain_name FROM information_schema.columns WHERE table_schema='BTCPayServer.Plugins.BTCPayRaffle' AND table_name='RaffleTickets' AND column_name='InvoiceId'"));
        Assert.Equal("2", await fixture.Scalar("SELECT count(*) FROM pg_constraint WHERE contypid IN ('public.description_domain'::regtype, 'public.invoice_domain'::regtype)"));
        Assert.Equal(0, await fixture.WidthAlterations());
    }

    [RaffleDatabaseFact]
    public async Task Custom_collation_defaults_nullability_and_rows_survive_widening()
    {
        await using var fixture = await Fixture.Create();
        await fixture.CreateShape("varchar(2000)", "varchar(50)");
        await fixture.Sql("""
ALTER TABLE "BTCPayServer.Plugins.BTCPayRaffle"."Raffles"
    ALTER COLUMN "Description" TYPE varchar(2000) COLLATE "C",
    ALTER COLUMN "Description" SET DEFAULT 'default-description';
ALTER TABLE "BTCPayServer.Plugins.BTCPayRaffle"."RaffleTickets"
    ALTER COLUMN "InvoiceId" TYPE varchar(50) COLLATE "C",
    ALTER COLUMN "InvoiceId" SET DEFAULT 'default-invoice';
""");
        await fixture.SeedRows();
        var rows = await fixture.Rows();
        var descriptionMetadata = await fixture.ColumnMetadata("Raffles", "Description");
        var invoiceMetadata = await fixture.ColumnMetadata("RaffleTickets", "InvoiceId");
        var history = await fixture.History();
        await fixture.Run();
        await fixture.Run();
        Assert.Equal("text:", await fixture.Shape("Raffles", "Description"));
        Assert.Equal("character varying:100", await fixture.Shape("RaffleTickets", "InvoiceId"));
        Assert.Equal(descriptionMetadata, await fixture.ColumnMetadata("Raffles", "Description"));
        Assert.Equal(invoiceMetadata, await fixture.ColumnMetadata("RaffleTickets", "InvoiceId"));
        Assert.Equal(rows, await fixture.Rows());
        Assert.Equal(history, await fixture.History());
        await fixture.WriteCurrentPayload();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _adminConnection;
        private readonly string _database;
        private readonly NpgsqlConnection _connection;
        private readonly RaffleDbContextFactory _factory;
        private readonly CaptureLogger _logger = new();
        public int Warnings => _logger.Warnings;

        private Fixture(string adminConnection, string database, NpgsqlConnection connection, string target)
        {
            _adminConnection = adminConnection;
            _database = database;
            _connection = connection;
            _factory = new RaffleDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = target }));
        }

        public static async Task<Fixture> Create()
        {
            var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION"));
            Assert.Contains(builder.Host, new[] { "127.0.0.1", "localhost", "::1" });
            Assert.StartsWith("cashu_", builder.Database);
            builder.Pooling = false;
            var admin = builder.ConnectionString;
            var database = "raffle_width247_" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
                await create.ExecuteNonQueryAsync();
            }
            builder.Database = database;
            var target = new NpgsqlConnection(builder.ConnectionString);
            await target.OpenAsync();
            return new Fixture(admin, database, target, builder.ConnectionString);
        }

        public Task Run() => new PluginMigrationRunner(_factory, _logger).ExecuteAsync();
        public async Task Sql(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection);
            await command.ExecuteNonQueryAsync();
        }
        public async Task<string> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection);
            return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
        }
        public Task<string> Shape(string table, string column) => Scalar($"SELECT data_type || ':' || COALESCE(character_maximum_length::text, '') FROM information_schema.columns WHERE table_schema='BTCPayServer.Plugins.BTCPayRaffle' AND table_name='{table}' AND column_name='{column}'");
        public Task<string> ColumnMetadata(string table, string column) => Scalar($"SELECT is_nullable || ':' || COALESCE(column_default, '') || ':' || COALESCE(collation_schema, '') || ':' || COALESCE(collation_name, '') FROM information_schema.columns WHERE table_schema='BTCPayServer.Plugins.BTCPayRaffle' AND table_name='{table}' AND column_name='{column}'");
        public async Task CreateShape(string description, string invoice)
        {
            await using var ctx = _factory.CreateContext();
            await RaffleSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
            await Sql($"ALTER TABLE \"BTCPayServer.Plugins.BTCPayRaffle\".\"Raffles\" ALTER COLUMN \"Description\" TYPE {description}; ALTER TABLE \"BTCPayServer.Plugins.BTCPayRaffle\".\"RaffleTickets\" ALTER COLUMN \"InvoiceId\" TYPE {invoice};");
        }
        public Task SeedRows(string description = "Legacy original", string invoice = "legacy-invoice") => Sql($$"""
INSERT INTO "BTCPayServer.Plugins.BTCPayRaffle"."Raffles" ("Id","Name","Description","StoreId","TicketPriceSats","TicketCurrency","TicketPrice","MaxTickets","Status","CreatedAt")
VALUES ('00000000-0000-0000-0000-000000000001','Legacy preserved','{{description}}','fixture-store',123,'EUR',12.50000000,17,1,'2026-10-01T12:00:00Z'),
('00000000-0000-0000-0000-000000000002','Null description',NULL,'fixture-store',456,'USD',4.25000000,NULL,0,'2026-10-02T12:00:00Z');
INSERT INTO "BTCPayServer.Plugins.BTCPayRaffle"."RaffleTickets" ("Id","RaffleId","TicketNumber","InvoiceId","IsManual","BuyerEmail","BuyerName","AllocatedAt")
VALUES ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000001',1,'{{invoice}}',false,'fixture@example.invalid','Fixture','2026-10-03T12:00:00Z');
INSERT INTO "BTCPayServer.Plugins.BTCPayRaffle"."RaffleDrawings" ("Id","RaffleId","DrawOrder","WinningTicketId","DrawnAt")
VALUES ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000001',1,'00000000-0000-0000-0000-000000000003','2026-10-04T12:00:00Z');
""");
        public async Task<string> Rows() => await Scalar("SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY r.\"Id\")::text,'[]') FROM \"BTCPayServer.Plugins.BTCPayRaffle\".\"Raffles\" r")
             + await Scalar("SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t.\"Id\")::text,'[]') FROM \"BTCPayServer.Plugins.BTCPayRaffle\".\"RaffleTickets\" t")
            + await Scalar("SELECT COALESCE(jsonb_agg(to_jsonb(d) ORDER BY d.\"Id\")::text,'[]') FROM \"BTCPayServer.Plugins.BTCPayRaffle\".\"RaffleDrawings\" d");
        public async Task<string> History()
        {
            await using var ctx = _factory.CreateContext();
            var applied = await ctx.Database.GetAppliedMigrationsAsync();
            // The factory uses the schema name as the history TABLE in public.
            var table = await Scalar("SELECT COALESCE(to_regclass('public.\"BTCPayServer.Plugins.BTCPayRaffle\"')::text,'')");
            var rows = table == "" ? "[]" : await Scalar("SELECT COALESCE(jsonb_agg(to_jsonb(h) ORDER BY h.\"MigrationId\")::text,'[]') FROM public.\"BTCPayServer.Plugins.BTCPayRaffle\" h");
            return string.Join(",", applied) + "|" + rows;
        }
        public Task WriteCurrentPayload() => Sql("""
INSERT INTO "BTCPayServer.Plugins.BTCPayRaffle"."Raffles" ("Id","Name","Description","StoreId","TicketPriceSats","TicketCurrency","TicketPrice","Status","CreatedAt")
VALUES ('00000000-0000-0000-0000-000000000011','Current payload',repeat('d',6000),'fixture-store',99,'EUR',9.75,0,'2026-10-04T12:00:00Z');
INSERT INTO "BTCPayServer.Plugins.BTCPayRaffle"."RaffleTickets" ("Id","RaffleId","TicketNumber","InvoiceId","IsManual","AllocatedAt")
VALUES ('00000000-0000-0000-0000-000000000013','00000000-0000-0000-0000-000000000011',1,repeat('i',80),false,'2026-10-04T12:00:00Z');
""");
        public Task AuditWidthAlterations() => Sql("""
CREATE TABLE public.width_ddl_audit (tag text NOT NULL);
CREATE FUNCTION public.audit_width_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $audit$
BEGIN
    IF strpos(current_query(), '$raffle_width_alignment$') > 0 THEN
        INSERT INTO public.width_ddl_audit(tag) VALUES (tg_tag);
    END IF;
END;
$audit$;
CREATE EVENT TRIGGER audit_width_ddl ON ddl_command_end WHEN TAG IN ('ALTER TABLE') EXECUTE FUNCTION public.audit_width_ddl();
""");
        public async Task<int> WidthAlterations() => int.Parse(await Scalar("SELECT count(*) FROM public.width_ddl_audit"), CultureInfo.InvariantCulture);
        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            await using var admin = new NpgsqlConnection(_adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class CaptureLogger : ILogger<PluginMigrationRunner>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings++;
        }
    }
}
