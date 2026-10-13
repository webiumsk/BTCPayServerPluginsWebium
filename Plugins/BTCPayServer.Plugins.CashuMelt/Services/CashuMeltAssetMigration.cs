using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.CashuMelt.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.CashuMelt.Services;

internal static class CashuMeltAssetMigration
{
    public static async Task ProtectExistingAsync(CashuMeltDbContextFactory factory, CashuMeltAssetProtector assets, CancellationToken ct)
    {
        await using var ctx = factory.CreateContext();
        var rows = await ctx.CashuMeltPaymentRequests.Where(r =>
            (r.MintedProofsJson != null && !r.MintedProofsJson.StartsWith("protected:v1:"))
            || (r.BlankOutputsJson != null && !r.BlankOutputsJson.StartsWith("protected:v1:"))).ToListAsync(ct);
        foreach (var row in rows)
        {
            await using var lease = await CashuMeltDatabaseLease.TryAcquireAsync(factory, "cashumelt:quote:" + row.QuoteId, false, ct);
            if (lease is null) continue;
            await ctx.Entry(row).ReloadAsync(ct);
            if (row.MintedProofsJson is not null && !row.MintedProofsJson.StartsWith("protected:v1:", StringComparison.Ordinal))
                row.MintedProofsJson = assets.Protect(row.MintedProofsJson);
            if (row.BlankOutputsJson is not null && !row.BlankOutputsJson.StartsWith("protected:v1:", StringComparison.Ordinal))
                row.BlankOutputsJson = assets.Protect(row.BlankOutputsJson);
            await ctx.SaveChangesAsync(ct);
        }
        await using var sweepLease = await CashuMeltDatabaseLease.TryAcquireAsync(factory, "cashumelt:sweep", false, ct);
        if (sweepLease is null) return;
        var change = await ctx.CashuMeltChangeProofs.Where(r => !r.Secret.StartsWith("protected:v1:")).ToListAsync(ct);
        foreach (var row in change) row.Secret = assets.Protect(row.Secret);
        await ctx.SaveChangesAsync(ct);
    }
}
