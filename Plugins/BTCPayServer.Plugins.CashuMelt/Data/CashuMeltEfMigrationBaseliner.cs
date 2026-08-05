#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.CashuMelt.Data;

/// <summary>
/// When tables were created by <see cref="CashuMeltSchemaCreator"/> before EF migrations were registered,
/// stamps the EF history table so <see cref="DatabaseFacade.MigrateAsync"/> only applies missing changes.
/// </summary>
internal static class CashuMeltEfMigrationBaseliner
{
    private const string Schema = "BTCPayServer.Plugins.CashuMelt";

    public static async Task<int> TryBaselineAsync(
        CashuMeltDbContext ctx,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var pending = (await ctx.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
            return 0;

        if (!await TableExistsAsync(ctx, "CashuMeltStoreSettings", cancellationToken))
            return 0;

        var history = ctx.Database.GetService<IHistoryRepository>();
        await history.CreateIfNotExistsAsync(cancellationToken);

        var toStamp = new List<string>();
        foreach (var migrationId in pending.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!await IsPhysicalSchemaAtLeastAsync(ctx, migrationId, cancellationToken))
                break;
            toStamp.Add(migrationId);
        }

        if (toStamp.Count == 0)
            return 0;

        var scripts = toStamp
            .Select(id => history.GetInsertScript(new HistoryRow(id, ProductInfo.GetVersion())))
            .ToArray();
        await ctx.Database.ExecuteSqlRawAsync(string.Concat(scripts), cancellationToken);

        logger.LogInformation(
            "CashuMelt baselined {Count} EF migration(s) already satisfied by existing schema: {Ids}",
            toStamp.Count,
            string.Join(", ", toStamp));

        return toStamp.Count;
    }

    private static async Task<bool> IsPhysicalSchemaAtLeastAsync(
        CashuMeltDbContext ctx,
        string migrationId,
        CancellationToken ct) =>
        migrationId switch
        {
            "20240319000000_InitialCreate" =>
                await TableExistsAsync(ctx, "CashuMeltStoreSettings", ct)
                && await TableExistsAsync(ctx, "CashuMeltPaymentRequests", ct),
            "20240320000000_AddMeltFields" =>
                await ColumnExistsAsync(ctx, "CashuMeltPaymentRequests", "MintedProofsJson", ct),
            "20260424120000_AddMerchantRiskControls" =>
                await ColumnExistsAsync(ctx, "CashuMeltStoreSettings", "TrustedMintUrls", ct),
            "20260517000000_AddRetryTracking" =>
                await ColumnExistsAsync(ctx, "CashuMeltPaymentRequests", "RetryCount", ct),
            "20260805000000_AddNut08Change" =>
                await TableExistsAsync(ctx, "CashuMeltChangeProofs", ct)
                && await ColumnExistsAsync(ctx, "CashuMeltPaymentRequests", "BlankOutputsJson", ct),
            _ => false
        };

    private static async Task<bool> TableExistsAsync(
        CashuMeltDbContext ctx, string table, CancellationToken ct) =>
        await ExistsQueryAsync(
            ctx,
            """
            SELECT EXISTS (
              SELECT 1 FROM information_schema.tables
              WHERE table_schema = {0} AND table_name = {1}
            )
            """,
            Schema,
            table,
            ct);

    private static async Task<bool> ColumnExistsAsync(
        CashuMeltDbContext ctx, string table, string column, CancellationToken ct) =>
        await ExistsQueryAsync(
            ctx,
            """
            SELECT EXISTS (
              SELECT 1 FROM information_schema.columns
              WHERE table_schema = {0} AND table_name = {1} AND column_name = {2}
            )
            """,
            Schema,
            table,
            column,
            ct);

    private static async Task<bool> ExistsQueryAsync(
        CashuMeltDbContext ctx,
        string sql,
        string p0,
        string p1,
        CancellationToken ct)
    {
        var rows = await ctx.Database
            .SqlQueryRaw<bool>(sql, p0, p1)
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    private static async Task<bool> ExistsQueryAsync(
        CashuMeltDbContext ctx,
        string sql,
        string p0,
        string p1,
        string p2,
        CancellationToken ct)
    {
        var rows = await ctx.Database
            .SqlQueryRaw<bool>(sql, p0, p1, p2)
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }
}
