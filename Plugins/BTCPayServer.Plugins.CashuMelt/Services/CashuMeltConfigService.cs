using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BTCPayServer.Plugins.CashuMelt.Services;

public class CashuMeltConfigService
{
    private readonly CashuMeltDbContextFactory _dbContextFactory;

    public CashuMeltConfigService(CashuMeltDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<CashuMeltStoreSettings?> GetSettingsAsync(string storeId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            return await ctx.CashuMeltStoreSettings
                .FirstOrDefaultAsync(s => s.StoreId == storeId, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            await EnsureSchemaAsync(cancellationToken);
            return null;
        }
    }

    public async Task<CashuMeltStoreSettings?> GetEnabledSettingsAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(storeId, cancellationToken);
        return settings is { Enabled: true } ? settings : null;
    }

    public async Task SaveSettingsAsync(CashuMeltStoreSettings settings, CancellationToken cancellationToken = default)
    {
        await SaveSettingsCoreAsync(settings, retryOnMissingTable: true, cancellationToken);
    }

    private async Task SaveSettingsCoreAsync(CashuMeltStoreSettings settings, bool retryOnMissingTable, CancellationToken cancellationToken)
    {
        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var existing = await ctx.CashuMeltStoreSettings.FindAsync([settings.StoreId], cancellationToken);

            var now = DateTimeOffset.UtcNow;
            if (existing != null)
            {
                existing.MintUrl = settings.MintUrl;
                existing.Unit = settings.Unit ?? "sat";
                existing.LightningAddress = settings.LightningAddress;
                existing.Enabled = settings.Enabled;
                existing.TrustedMintUrls = settings.TrustedMintUrls;
                existing.MaxMeltFeeReserveSats = settings.MaxMeltFeeReserveSats;
                existing.MaxMeltFeeReservePercentOfMinted = settings.MaxMeltFeeReservePercentOfMinted;
                existing.UpdatedAt = now;
                ctx.CashuMeltStoreSettings.Update(existing);
            }
            else
            {
                settings.CreatedAt = now;
                settings.UpdatedAt = now;
                settings.Unit ??= "sat";
                await ctx.CashuMeltStoreSettings.AddAsync(settings, cancellationToken);
            }

            await ctx.SaveChangesAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01" && retryOnMissingTable)
        {
            await EnsureSchemaAsync(cancellationToken);
            await SaveSettingsCoreAsync(settings, retryOnMissingTable: false, cancellationToken);
        }
    }

    public async Task DeleteSettingsAsync(string storeId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var existing = await ctx.CashuMeltStoreSettings.FindAsync([storeId], cancellationToken);
            if (existing != null)
            {
                ctx.CashuMeltStoreSettings.Remove(existing);
                await ctx.SaveChangesAsync(cancellationToken);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            await EnsureSchemaAsync(cancellationToken);
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var bootstrapCtx = _dbContextFactory.CreateContext();
        await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(bootstrapCtx, cancellationToken);
    }
}
