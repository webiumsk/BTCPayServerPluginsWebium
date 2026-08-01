#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Fio;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>
/// Generic polling loop for confirmation backends that need it: the NOP
/// Lite REST poller (v0.2) and the Fio token API (v0.6); a future
/// aggregator backend plugs in the same way. Polls only stores that have at
/// least one PENDING payment request - idle stores cost nothing.
/// </summary>
public class SepaPollingHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialLookback = TimeSpan.FromHours(2); // NOP notification retention
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly SepaConfigService _configService;
    private readonly NopNotificationProcessor _processor;
    private readonly FioApiClient _fioClient;
    private readonly FioTransactionProcessor _fioProcessor;
    private readonly SepaMatchingService _matchingService;
    private readonly ILogger<SepaPollingHostedService> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSuccessfulPoll = new();

    public SepaPollingHostedService(
        SepaDbContextFactory dbContextFactory,
        SepaConfigService configService,
        NopNotificationProcessor processor,
        FioApiClient fioClient,
        FioTransactionProcessor fioProcessor,
        SepaMatchingService matchingService,
        ILogger<SepaPollingHostedService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _configService = configService;
        _processor = processor;
        _fioClient = fioClient;
        _fioProcessor = fioProcessor;
        _matchingService = matchingService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SEPA polling tick failed");
            }
        } while (await timer.WaitNextTickSafeAsync(stoppingToken));
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        List<SepaStoreSettings> stores;
        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var storeIdsWithPending = await ctx.SepaPaymentRequests
                .AsNoTracking()
                .Where(r => r.State == SepaPaymentRequestState.Pending)
                .Select(r => r.StoreId)
                .Distinct()
                .ToListAsync(ct);

            if (storeIdsWithPending.Count == 0)
                return;

            stores = await ctx.SepaStoreSettings
                .AsNoTracking()
                .Where(s => s.Enabled
                            && (s.ConfirmationBackend == NopRestPollerSource.BackendId
                                || s.ConfirmationBackend == FioSource.BackendId)
                            && storeIdsWithPending.Contains(s.StoreId))
                .ToListAsync(ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            return; // tables not migrated yet
        }

        foreach (var settings in stores)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (settings.ConfirmationBackend == FioSource.BackendId)
                    await PollFioStoreAsync(settings, ct);
                else
                    await PollStoreAsync(settings, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Confirmation poll failed for store {StoreId} ({Backend})",
                    settings.StoreId, settings.ConfirmationBackend);
            }
        }
    }

    private async Task PollStoreAsync(SepaStoreSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.NopPokladnica))
            return;

        var credentials = _configService.GetCredentials(settings);
        if (!credentials.HasNopCertificate)
            return;

        var startedAt = DateTimeOffset.UtcNow;
        var dateFrom = _lastSuccessfulPoll.TryGetValue(settings.StoreId, out var last)
            ? last - Overlap
            : startedAt - InitialLookback;

        using var client = NopRestClient.Create(credentials, _logger);
        var json = await client.GetAllTransactionsAsync($"POKLADNICA-{settings.NopPokladnica}", dateFrom, ct);

        if (json.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var element in json.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var notification = _processor.Parse(element.GetRawText());
                    if (notification is null)
                        continue;

                    var confirmed = _processor.ToConfirmedPayment(notification, settings.Iban);
                    if (confirmed is null)
                        continue;

                    await _matchingService.ProcessAsync(NopRestPollerSource.BackendId, confirmed, settings.AmountTolerance, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One broken notification must not sink the rest of the
                    // batch; the checkpoint below still advances - the dedup
                    // key makes any re-delivery harmless anyway.
                    _logger.LogWarning(ex,
                        "NOP REST notification processing failed for store {StoreId}; continuing with the batch",
                        settings.StoreId);
                }
            }
        }

        // Checkpoint advances only after the whole batch was fetched and
        // walked - a thrown fetch keeps the previous window so unprocessed
        // confirmations are retrieved again next tick.
        _lastSuccessfulPoll[settings.StoreId] = startedAt;
    }

    /// <summary>
    /// Fio: the cursor ("zarážka") lives server-side per token and advances
    /// automatically on every non-empty fetch, so there is no local
    /// checkpoint to keep. Individual broken movements never sink the batch
    /// - the dedup key (fio:movementId) makes re-deliveries harmless.
    /// </summary>
    private async Task PollFioStoreAsync(SepaStoreSettings settings, CancellationToken ct)
    {
        var credentials = _configService.GetCredentials(settings);
        if (!credentials.HasFioToken)
            return;

        using var document = await _fioClient.GetLastTransactionsAsync(credentials.FioToken!, ct);
        if (document is null)
            return; // 30 s rate limit - next tick catches up

        foreach (var confirmed in _fioProcessor.Parse(document))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _matchingService.ProcessAsync(FioSource.BackendId, confirmed, settings.AmountTolerance, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Fio movement processing failed for store {StoreId}; continuing with the batch",
                    settings.StoreId);
            }
        }
    }
}
