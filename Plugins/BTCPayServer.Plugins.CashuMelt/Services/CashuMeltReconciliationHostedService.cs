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
/// Does not custodian customer ecash: it only completes forwarding and invoice recording already designed into CashuMelt.
/// </summary>
public sealed class CashuMeltReconciliationHostedService : BackgroundService
{
    private const int MaxAutoRetries = 20;

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
            .Select(r => r.QuoteId)
            .ToListAsync(ct);

        foreach (var quoteId in meltCompleteIds)
        {
            if (!await IncrementRetryAndShouldProceedAsync(dbFactory, quoteId, ct))
                continue;
            await paymentService.CheckAndRecordPaymentAsync(quoteId, ct);
        }

        // Lightly poll stale PENDING rows (quote not paid yet; expires naturally).
        // No retry count tracked here — pending rows expire via mint and transition to FAILED.
        if (tick % 3 == 0)
        {
            var stale = DateTimeOffset.UtcNow.AddMinutes(-2);
            var pendingIds = await ctx.CashuMeltPaymentRequests.AsNoTracking()
                .Where(r => r.SettlementState == "PENDING"
                            && r.CreatedAt < stale
                            && r.CreatedAt > DateTimeOffset.UtcNow.AddDays(-14))
                .OrderBy(r => r.CreatedAt)
                .Take(25)
                .Select(r => r.QuoteId)
                .ToListAsync(ct);

            foreach (var quoteId in pendingIds)
                await paymentService.CheckAndRecordPaymentAsync(quoteId, ct);
        }

        // Retry FAILED rows that still have stored proofs (can attempt re-melt).
        if (tick % 5 == 0)
        {
            var failedRows = await ctx.CashuMeltPaymentRequests.AsNoTracking()
                .Where(r => r.SettlementState == "FAILED"
                            && r.MintedProofsJson != null
                            && r.MintedProofsJson != ""
                            && !r.NeedsManualReview)
                .OrderBy(r => r.CreatedAt)
                .Take(12)
                .Select(r => new { r.QuoteId, r.StoreId })
                .ToListAsync(ct);

            foreach (var row in failedRows)
            {
                if (!await IncrementRetryAndShouldProceedAsync(dbFactory, row.QuoteId, ct))
                    continue;
                await paymentService.RetrySettlementAsync(row.StoreId, row.QuoteId, ct);
            }
        }
    }

    /// <summary>
    /// Increments the retry count for the given quote. Returns false (and sets
    /// <c>NeedsManualReview</c>) when the count reaches <see cref="MaxAutoRetries"/>.
    /// </summary>
    private async Task<bool> IncrementRetryAndShouldProceedAsync(
        CashuMeltDbContextFactory dbFactory,
        string quoteId,
        CancellationToken ct)
    {
        await using var ctx = dbFactory.CreateContext();
        var req = await ctx.CashuMeltPaymentRequests
            .FirstOrDefaultAsync(r => r.QuoteId == quoteId, ct);

        if (req is null || req.NeedsManualReview || req.SettlementState == "SETTLED")
            return false;

        req.RetryCount++;

        if (req.RetryCount >= MaxAutoRetries)
        {
            req.NeedsManualReview = true;
            req.FailureReasonCode = CashuMeltFailureReasons.MaxRetriesExceeded;
            req.SettlementError   = CashuMeltFailureReasons.Describe(CashuMeltFailureReasons.MaxRetriesExceeded);
            await ctx.SaveChangesAsync(ct);
            _logger.LogWarning(
                "cashumelt_manual_review_required quote={QuoteId} invoice={InvoiceId} retryCount={RetryCount} settlementState={State} — exceeded {Max} auto-retry attempts",
                quoteId, req.InvoiceId, req.RetryCount, req.SettlementState, MaxAutoRetries);
            return false;
        }

        await ctx.SaveChangesAsync(ct);
        return true;
    }
}
