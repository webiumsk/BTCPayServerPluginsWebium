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
/// Tracks retry count per row; after <see cref="MaxAutoRetries"/> consecutive auto-retry failures
/// the row is flagged <c>NeedsManualReview</c> and skipped until manually retried via UI/API.
/// Recovers temporary ecash held by BTCPay until forwarding or merchant recovery completes.
/// </summary>
public sealed class CashuMeltReconciliationHostedService : BackgroundService
{
    private const int MaxAutoRetries = CashuMeltPaymentService.MaxAutomaticRetries;

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

        // Retry MELT_COMPLETE rows (melt succeeded, only BTCPay accounting pending).
        var meltCompleteIds = await ctx.CashuMeltPaymentRequests.AsNoTracking()
            .Where(r => r.SettlementState == "MELT_COMPLETE" && !r.NeedsManualReview)
            .OrderBy(r => r.CreatedAt)
            .Take(35)
            .Select(r => new { r.QuoteId, r.StoreId })
            .ToListAsync(ct);

        foreach (var row in meltCompleteIds)
            await paymentService.RetrySettlementAutomaticallyAsync(row.StoreId, row.QuoteId, ct);

        // Poll stale funding quotes, including legacy/failed rows without recovery assets.
        // Unfunded rows remain uncounted; durable recovery uses the capped, quote-locked entry point.
        if (tick % 3 == 0)
        {
            var stale = DateTimeOffset.UtcNow.AddMinutes(-2);
            var pendingIds = await ctx.CashuMeltPaymentRequests.AsNoTracking()
                .Where(r => (r.SettlementState == "PENDING"
                            || ((r.SettlementState == "FAILED" || (r.SettlementState == "MANUAL_REVIEW" && r.SettingsSnapshotJson == null))
                                && r.MintedProofsJson == null && r.MintIssuanceJson == null && r.BlankOutputsJson == null && r.MeltQuoteId == null))
                            && r.CreatedAt < stale
                            && !(r.SettlementState == "FAILED" && r.FailureReasonCode == CashuMeltFailureReasons.MintPollError)
                            && !(r.NeedsManualReview && (r.MintedProofsJson != null || r.MintIssuanceJson != null
                                || r.BlankOutputsJson != null || r.MeltQuoteId != null)))
                .OrderBy(r => r.LastCheckedAt ?? r.CreatedAt)
                .Take(25)
                .Select(r => new { r.QuoteId, r.StoreId,
                    HasAssets = r.MintedProofsJson != null || r.MintIssuanceJson != null || r.BlankOutputsJson != null || r.MeltQuoteId != null })
                .ToListAsync(ct);

            foreach (var row in pendingIds)
            {
                if (row.HasAssets) await paymentService.RetrySettlementAutomaticallyAsync(row.StoreId, row.QuoteId, ct);
                else await paymentService.CheckAndRecordPaymentAsync(row.QuoteId, ct);
            }
        }

        // Sweep accumulated NUT-08 change proofs to the merchant Lightning address (~15 min
        // cadence). Bounded by its own deadline so a slow mint cannot starve the FAILED-row
        // retries below; an interrupted sweep resumes safely on the next tick.
        if (tick % 20 == 0)
        {
            using var sweepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sweepCts.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await paymentService.SweepAvailableChangeAsync(sweepCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("CashuMelt change sweep exceeded its 5 minute budget; continuing with the tick");
            }
        }

        // Retry FAILED rows that still have stored proofs (can attempt re-melt).
        if (tick % 5 == 0)
        {
            var failedRows = await ctx.CashuMeltPaymentRequests.AsNoTracking()
                .Where(r => r.SettlementState == "FAILED"
                            && (r.MintedProofsJson != null || r.MintIssuanceJson != null)
                            && !r.NeedsManualReview)
                .OrderBy(r => r.CreatedAt)
                .Take(12)
                .Select(r => new { r.QuoteId, r.StoreId })
                .ToListAsync(ct);

            foreach (var row in failedRows)
                await paymentService.RetrySettlementAutomaticallyAsync(row.StoreId, row.QuoteId, ct);
        }
    }

}
