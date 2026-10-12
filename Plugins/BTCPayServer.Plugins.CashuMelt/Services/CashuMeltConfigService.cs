using System;
using System.Linq;
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
            await using var lease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + settings.StoreId, false, cancellationToken)
                ?? throw new InvalidOperationException("Cashu settlement in progress; try again after it finishes");
            var existing = await ctx.CashuMeltStoreSettings.FindAsync([settings.StoreId], cancellationToken);
            if (existing is null || existing.MintUrl != settings.MintUrl || existing.LightningAddress != settings.LightningAddress
                || existing.Unit != settings.Unit || existing.Enabled != settings.Enabled)
                await AssertNoUnresolvedFundsAsync(ctx, settings.StoreId, cancellationToken);

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
            await using var lease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + storeId, false, cancellationToken)
                ?? throw new InvalidOperationException("Cashu settlement in progress");
            await AssertNoUnresolvedFundsAsync(ctx, storeId, cancellationToken);
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

    public async Task<(int Payments, int Change)> GetUnresolvedFundsAsync(string storeId, CancellationToken ct)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var payments = await ctx.CashuMeltPaymentRequests.CountAsync(r => r.StoreId == storeId &&
            ((r.SettlementState == null || (r.SettlementState != "SETTLED" && r.SettlementState != "EXPIRED_UNPAID"))
             || r.MintedProofsJson != null || r.MintIssuanceJson != null || r.BlankOutputsJson != null), ct);
        var change = await ctx.CashuMeltChangeProofs.CountAsync(r => r.StoreId == storeId &&
            ((r.State != "SWEPT" && r.State != "RECOVERED") || r.SweepIntentJson != null), ct);
        return (payments, change);
    }

    private static async Task AssertNoUnresolvedFundsAsync(CashuMeltDbContext ctx, string storeId, CancellationToken ct)
    {
        if (await ctx.CashuMeltPaymentRequests.AnyAsync(r => r.StoreId == storeId &&
            ((r.SettlementState == null || (r.SettlementState != "SETTLED" && r.SettlementState != "EXPIRED_UNPAID"))
             || r.MintedProofsJson != null || r.MintIssuanceJson != null || r.BlankOutputsJson != null), ct)
            || await ctx.CashuMeltChangeProofs.AnyAsync(r => r.StoreId == storeId &&
                ((r.State != "SWEPT" && r.State != "RECOVERED") || r.SweepIntentJson != null), ct))
            throw new InvalidOperationException("Outstanding Cashu settlements or change must be recovered before changing this wallet");
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var bootstrapCtx = _dbContextFactory.CreateContext();
        await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(bootstrapCtx, cancellationToken);
    }
}
