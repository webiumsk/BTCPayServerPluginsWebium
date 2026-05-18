using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BTCPayServer.Plugins.CashuMelt;

public class PluginMigrationRunner : IStartupTask
{
    private readonly CashuMeltDbContextFactory _dbContextFactory;
    private readonly CashuMeltConfigService _configService;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<PluginMigrationRunner> _logger;

    public PluginMigrationRunner(
        CashuMeltDbContextFactory dbContextFactory,
        CashuMeltConfigService configService,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ILogger<PluginMigrationRunner> logger)
    {
        _dbContextFactory = dbContextFactory;
        _configService    = configService;
        _storeRepository  = storeRepository;
        _handlers         = handlers;
        _logger           = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Migrating CashuMelt plugin database");
        await using var ctx = _dbContextFactory.CreateContext();

        // Rename legacy schema/tables from the old "Cashu" plugin name if they exist.
        // This runs before EF migrations so the new names are in place when EF runs.
        await MigrateFromOldSchemaAsync(ctx, cancellationToken);

        try
        {
            await ApplyEfMigrationsAsync(ctx, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CashuMelt EF migrations failed; falling back to raw SQL schema creator.");
        }

        // Always run the idempotent schema creator: ensures all columns exist
        // regardless of whether the table was created by EF migrations or raw SQL.
        await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx, cancellationToken);

        // Ensure every store that has CashuMelt settings in the plugin DB also has the
        // CASHU payment method registered in BTCPay's DerivationStrategies.
        // Stores configured via the API before this fix was deployed would be missing
        // this entry, causing "No wallet has been linked" errors on invoice creation.
        await EnsurePaymentMethodRegisteredAsync(ctx, cancellationToken);
    }

    private async Task ApplyEfMigrationsAsync(CashuMeltDbContext ctx, CancellationToken cancellationToken)
    {
        await CashuMeltEfMigrationBaseliner.TryBaselineAsync(ctx, _logger, cancellationToken);

        var pending = await ctx.Database.GetPendingMigrationsAsync(cancellationToken);
        if (!pending.Any())
            return;

        _logger.LogInformation("Applying {Count} CashuMelt migration(s)", pending.Count());
        try
        {
            await ctx.Database.MigrateAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P07" or "42701")
        {
            _logger.LogWarning(
                "CashuMelt migration hit an already-existing object ({SqlState}); baselining history and retrying.",
                ex.SqlState);
            await CashuMeltEfMigrationBaseliner.TryBaselineAsync(ctx, _logger, cancellationToken);
            await ctx.Database.MigrateAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Renames the PostgreSQL schema and tables from the legacy "Cashu" plugin name to "CashuMelt".
    /// Safe to call repeatedly – does nothing if old schema is absent or migration already done.
    /// </summary>
    private async Task MigrateFromOldSchemaAsync(CashuMeltDbContext ctx, CancellationToken ct)
    {
        const string oldSchema = "BTCPayServer.Plugins.Cashu";
        const string newSchema = "BTCPayServer.Plugins.CashuMelt";

        // Rename schema (fails silently if old schema doesn't exist or new schema already exists)
        try
        {
            await ctx.Database.ExecuteSqlRawAsync(
                $@"ALTER SCHEMA ""{oldSchema}"" RENAME TO ""{newSchema}""", ct);
            _logger.LogInformation("Renamed PostgreSQL schema from {Old} to {New}", oldSchema, newSchema);
        }
        catch (PostgresException ex) when (ex.SqlState is "3F000" or "42P06")
        {
            // 3F000 = invalid_schema_name (old schema absent – nothing to do)
            // 42P06 = duplicate_schema   (new schema already exists – already migrated)
            return;
        }

        // Schema renamed successfully → also rename the tables that got a "Melt" infix.
        // Old name                  → New name
        // CashuStoreSettings        → CashuMeltStoreSettings
        // CashuPaymentRequests      → CashuMeltPaymentRequests
        await RenameTableIfExistsAsync(ctx, newSchema, "CashuStoreSettings",   "CashuMeltStoreSettings",   ct);
        await RenameTableIfExistsAsync(ctx, newSchema, "CashuPaymentRequests", "CashuMeltPaymentRequests", ct);
    }

    private async Task RenameTableIfExistsAsync(
        CashuMeltDbContext ctx, string schema, string oldTable, string newTable, CancellationToken ct)
    {
        try
        {
            await ctx.Database.ExecuteSqlRawAsync(
                $@"ALTER TABLE ""{schema}"".""{oldTable}"" RENAME TO ""{newTable}""", ct);
            _logger.LogInformation("Renamed table {Old} → {New} in schema {Schema}", oldTable, newTable, schema);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01")
        {
            // 42P01 = undefined_table – table already has the new name or never existed
        }
    }

    private async Task EnsurePaymentMethodRegisteredAsync(CashuMeltDbContext ctx, CancellationToken ct)
    {
        if (!_handlers.Support(CashuMeltPlugin.CashuMeltPaymentMethodId))
            return;

        Data.Entities.CashuMeltStoreSettings[] allSettings;
        try
        {
            allSettings = await ctx.CashuMeltStoreSettings.ToArrayAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            // Table doesn't exist yet – nothing to migrate
            _logger.LogWarning(ex, "CashuMeltStoreSettings table not ready; skipping payment method registration.");
            return;
        }

        var handler = _handlers[CashuMeltPlugin.CashuMeltPaymentMethodId];
        int registeredCount = 0;

        foreach (var settings in allSettings)
        {
            var store = await _storeRepository.FindStore(settings.StoreId);
            if (store is null) continue;

            // Check if CASHU is already in DerivationStrategies
            var existing = store.GetPaymentMethodConfig(CashuMeltPlugin.CashuMeltPaymentMethodId);
            if (existing is not null) continue;

            // Register the payment method so BTCPay knows the store has at least one
            store.SetPaymentMethodConfig(handler, new CashuMeltPaymentMethodConfig { Enabled = settings.Enabled });
            await _storeRepository.UpdateStore(store);
            registeredCount++;
            _logger.LogInformation("Registered CashuMelt payment method for store {StoreId}", settings.StoreId);
        }

        if (registeredCount > 0)
            _logger.LogInformation("Registered CashuMelt payment method for {Count} existing store(s)", registeredCount);
    }
}
