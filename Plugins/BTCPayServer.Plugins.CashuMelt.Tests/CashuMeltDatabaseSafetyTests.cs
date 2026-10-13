using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

public sealed class CashuDatabaseFactAttribute : FactAttribute
{
    public CashuDatabaseFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")))
            Skip = "Requires an isolated PostgreSQL database (CASHUMELT_TEST_CONNECTION). CI supplies it.";
    }
}

[CollectionDefinition("Cashu database", DisableParallelization = true)]
public sealed class CashuDatabaseCollection { }

[Collection("Cashu database")]
public class CashuMeltDatabaseSafetyTests
{
    private static CashuMeltDbContextFactory Factory() => new(Options.Create(new DatabaseOptions
    { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));

    private static async Task<CashuMeltDbContextFactory> Ready()
    {
        var factory = Factory();
        await using var ctx = factory.CreateContext();
        await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
        return factory;
    }

    [CashuDatabaseFact]
    public async Task Orphaned_assets_cannot_be_reassigned_by_recreating_store_settings()
    {
        var factory = await Ready(); var store = Guid.NewGuid().ToString("N");
        await using var ctx = factory.CreateContext();
        ctx.CashuMeltPaymentRequests.Add(new CashuMeltPaymentRequest { QuoteId = Guid.NewGuid().ToString("N"), StoreId = store, InvoiceId = "old", CreatedAt = DateTimeOffset.UtcNow, SettlementState = "MANUAL_REVIEW" });
        await ctx.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CashuMeltConfigService(factory).SaveSettingsAsync(new CashuMeltStoreSettings { StoreId = store, MintUrl = "https://replacement.example", LightningAddress = "replacement@example.com", Enabled = true }));
    }

    [CashuDatabaseFact]
    public async Task Fresh_database_applies_the_entire_EF_migration_chain()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION"));
        Assert.Equal("cashu_safety", connection.Database); // Never create/drop a production database.
        var database = "cashu_safety_" + Guid.NewGuid().ToString("N");
        await using var admin = new Npgsql.NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new Npgsql.NpgsqlCommand("CREATE DATABASE \"" + database + "\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connection.ConnectionString }));
            await using var ctx = factory.CreateContext();
            await ctx.Database.MigrateAsync();
            await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
            Assert.Contains("20261012210000_AddSettlementSafety", await ctx.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            await using var drop = new Npgsql.NpgsqlCommand("DROP DATABASE \"" + database + "\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [CashuDatabaseFact]
    public async Task Raw_schema_and_EF_baseline_agree_and_survive_repeated_startup()
    {
        var factory = await Ready();
        await using var ctx = factory.CreateContext();
        await CashuMeltEfMigrationBaseliner.TryBaselineAsync(ctx, NullLogger.Instance);
        await ctx.Database.MigrateAsync();
        await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
        Assert.Contains("20261012210000_AddSettlementSafety", await ctx.Database.GetAppliedMigrationsAsync());
    }

    [CashuDatabaseFact]
    public async Task Independent_instances_exclude_duplicate_quote_work_and_wallet_changes()
    {
        var id = Guid.NewGuid().ToString("N");
        var factory = Factory();
        var quote = await CashuMeltDatabaseLease.TryAcquireAsync(factory, "quote:" + id, false, default);
        Assert.NotNull(quote);
        Assert.Null(await CashuMeltDatabaseLease.TryAcquireAsync(Factory(), "quote:" + id, false, default));
        await quote!.DisposeAsync();
        await using var resumed = await CashuMeltDatabaseLease.TryAcquireAsync(Factory(), "quote:" + id, false, default);
        Assert.NotNull(resumed);
        await using var payment = await CashuMeltDatabaseLease.TryAcquireAsync(factory, "store:" + id, true, default);
        await using var anotherPayment = await CashuMeltDatabaseLease.TryAcquireAsync(Factory(), "store:" + id, true, default);
        Assert.NotNull(anotherPayment);
        Assert.Null(await CashuMeltDatabaseLease.TryAcquireAsync(Factory(), "store:" + id, false, default));
    }

    [CashuDatabaseFact]
    public async Task Inflight_payment_blocks_wallet_change_but_fee_only_update_is_allowed()
    {
        var factory = await Ready();
        var service = new CashuMeltConfigService(factory);
        var store = Guid.NewGuid().ToString("N");
        var settings = new CashuMeltStoreSettings { StoreId = store, MintUrl = "https://mint.example", LightningAddress = "original@example.com", Enabled = true };
        await service.SaveSettingsAsync(settings);
        await using var ctx = factory.CreateContext();
        var request = new CashuMeltPaymentRequest { QuoteId = Guid.NewGuid().ToString("N"), StoreId = store, InvoiceId = "invoice", CreatedAt = DateTimeOffset.UtcNow, SettlementState = "MANUAL_REVIEW", MintIssuanceJson = "durable-recovery" };
        ctx.CashuMeltPaymentRequests.Add(request);
        await ctx.SaveChangesAsync();
        settings.MaxMeltFeeReserveSats = 10;
        await service.SaveSettingsAsync(settings);
        settings.LightningAddress = "replacement@example.com";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSettingsAsync(settings));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteSettingsAsync(store));
        Assert.Equal((1, 0), await service.GetUnresolvedFundsAsync(store, default));
        request.SettlementState = "SETTLED"; // Even a settled row still carrying assets is unresolved.
        await ctx.SaveChangesAsync();
        Assert.Equal((1, 0), await service.GetUnresolvedFundsAsync(store, default));
        request.MintIssuanceJson = null;
        await ctx.SaveChangesAsync();
        await service.SaveSettingsAsync(settings);
    }

    [CashuDatabaseFact]
    public async Task Exported_change_blocks_wallet_change_until_verified_consumed()
    {
        var factory = await Ready();
        var store = Guid.NewGuid().ToString("N");
        var config = new CashuMeltConfigService(factory);
        var settings = new CashuMeltStoreSettings { StoreId = store, MintUrl = "https://mint.example", LightningAddress = "a@example.com", Enabled = true };
        await config.SaveSettingsAsync(settings);
        await using var ctx = factory.CreateContext();
        var row = new CashuMeltChangeProof { StoreId = store, MintUrl = settings.MintUrl, Amount = 1, KeysetId = "keyset", C = Guid.NewGuid().ToString("N"), Secret = "bearer-secret", State = "EXPORTED", CreatedAt = DateTimeOffset.UtcNow };
        ctx.CashuMeltChangeProofs.Add(row); await ctx.SaveChangesAsync();
        Assert.Equal((0, 1), await config.GetUnresolvedFundsAsync(store, default));
        settings.Enabled = false;
        await config.SaveSettingsAsync(settings); // Stop new exposure without changing destination.
        Assert.False((await config.GetSettingsAsync(store))!.Enabled);
        Assert.Equal((0, 1), await config.GetUnresolvedFundsAsync(store, default));
        settings.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => config.SaveSettingsAsync(settings));
        await Assert.ThrowsAsync<InvalidOperationException>(() => config.DeleteSettingsAsync(store));
        settings.Enabled = false;
        settings.LightningAddress = "replacement@example.com";
        await Assert.ThrowsAsync<InvalidOperationException>(() => config.SaveSettingsAsync(settings));
        settings.LightningAddress = "a@example.com";
        row.State = "RECOVERED"; row.Secret = "consumed"; await ctx.SaveChangesAsync();
        Assert.Equal((0, 0), await config.GetUnresolvedFundsAsync(store, default));
        await config.SaveSettingsAsync(settings);
    }

    [CashuDatabaseFact]
    public async Task Legacy_assets_are_encrypted_idempotently_without_losing_recovery_data()
    {
        var factory = await Ready();
        var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
        await using var ctx = factory.CreateContext();
        var request = new CashuMeltPaymentRequest { QuoteId = Guid.NewGuid().ToString("N"), StoreId = "legacy", InvoiceId = "old", CreatedAt = DateTimeOffset.UtcNow, MintedProofsJson = "[secret-proof]", BlankOutputsJson = "[secret-blinding]" };
        ctx.CashuMeltPaymentRequests.Add(request); await ctx.SaveChangesAsync();
        await CashuMeltAssetMigration.ProtectExistingAsync(factory, assets, default);
        await ctx.Entry(request).ReloadAsync();
        Assert.Equal("[secret-proof]", assets.Unprotect(request.MintedProofsJson!));
        Assert.Equal("[secret-blinding]", assets.Unprotect(request.BlankOutputsJson!));
        var ciphertext = request.MintedProofsJson;
        await CashuMeltAssetMigration.ProtectExistingAsync(factory, assets, default);
        await ctx.Entry(request).ReloadAsync();
        Assert.Equal(ciphertext, request.MintedProofsJson);
    }
}
