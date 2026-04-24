#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.CashuMelt.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>
/// Periodically retries BTCPay accounting after a successful melt (<c>MELT_COMPLETE</c>) and lightly polls
/// stale <c>PENDING</c> / retriable <c>FAILED</c> rows so checkout does not have to stay open.
/// Does not custodian customer ecash: it only completes forwarding and invoice recording already designed into CashuMelt.
/// </summary>
public sealed class CashuMeltReconciliationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CashuMeltReconciliationHostedService> _logger;

    public CashuMeltReconciliationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<CashuMeltReconciliationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(45));
        var tick = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            tick++;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var paymentService = scope.ServiceProvider.GetRequiredService<CashuMeltPaymentService>();
                var dbFactory      = scope.ServiceProvider.GetRequiredService<CashuMeltDbContextFactory>();
                await RunTickAsync(paymentService, dbFactory, tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CashuMelt reconciliation tick failed");
            }
        }
    }

    private async Task RunTickAsync(
        CashuMeltPaymentService paymentService,
        CashuMeltDbContextFactory dbFactory,
        int tick,
        CancellationToken ct)
    {
        await using var ctx = dbFactory.CreateContext();

        var meltCompleteIds = await ctx.CashuMeltPaymentRequests.AsNoTracking()
            .Where(r => r.SettlementState == "MELT_COMPLETE")
            .OrderBy(r => r.CreatedAt)
            .Take(35)
            .Select(r => r.QuoteId)
            .ToListAsync(ct);

        foreach (var quoteId in meltCompleteIds)
        {
            await paymentService.CheckAndRecordPaymentAsync(quoteId, ct);
        }

        if (tick % 3 == 0)
        {
            var stale = DateTimeOffset.UtcNow.AddMinutes(-2);
            var pendingIds = await ctx.CashuMeltPaymentRequests.AsNoTracking()
                .Where(r => r.SettlementState == "PENDING" && r.CreatedAt < stale && r.CreatedAt > DateTimeOffset.UtcNow.AddDays(-14))
                .OrderBy(r => r.CreatedAt)
                .Take(25)
                .Select(r => r.QuoteId)
                .ToListAsync(ct);

            foreach (var quoteId in pendingIds)
                await paymentService.CheckAndRecordPaymentAsync(quoteId, ct);
        }

        if (tick % 5 == 0)
        {
            var failedRows = await ctx.CashuMeltPaymentRequests.AsNoTracking()
                .Where(r => r.SettlementState == "FAILED" && r.MintedProofsJson != null && r.MintedProofsJson != "")
                .OrderBy(r => r.CreatedAt)
                .Take(12)
                .Select(r => new { r.QuoteId, r.StoreId })
                .ToListAsync(ct);

            foreach (var row in failedRows)
                await paymentService.RetrySettlementAsync(row.StoreId, row.QuoteId, ct);
        }
    }
}
