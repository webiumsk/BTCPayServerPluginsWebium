#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;

/// <summary>
/// Maintains one mTLS MQTT connection per store with the nop-mqtt backend:
/// subscribes to {VATSK}/{POKLADNICA}/# (QoS 1), feeds notifications through
/// the shared processor into the matching service, reconnects with the
/// manual's exponential backoff, and catches up missed notifications via the
/// REST getAllTransactions (2-hour retention) after every (re)connect.
/// </summary>
public class NopMqttListener : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(5);

    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly SepaConfigService _configService;
    private readonly NopNotificationProcessor _processor;
    private readonly SepaMatchingService _matchingService;
    private readonly ILogger<NopMqttListener> _logger;

    private readonly Dictionary<string, StoreConnection> _connections = new();

    public NopMqttListener(
        SepaDbContextFactory dbContextFactory,
        SepaConfigService configService,
        NopNotificationProcessor processor,
        SepaMatchingService matchingService,
        ILogger<NopMqttListener> logger)
    {
        _dbContextFactory = dbContextFactory;
        _configService = configService;
        _processor = processor;
        _matchingService = matchingService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            try
            {
                await RefreshConnectionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NOP MQTT connection refresh failed");
            }
        } while (await timer.WaitNextTickSafeAsync(stoppingToken));

        foreach (var connection in _connections.Values)
            await connection.DisposeAsync();
        _connections.Clear();
    }

    private async Task RefreshConnectionsAsync(CancellationToken ct)
    {
        List<SepaStoreSettings> stores;
        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            stores = await ctx.SepaStoreSettings
                .AsNoTracking()
                .Where(s => s.Enabled && s.ConfirmationBackend == NopMqttSource.BackendId)
                .ToListAsync(ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            return; // tables not migrated yet
        }

        var wanted = new HashSet<string>();
        foreach (var settings in stores)
        {
            if (string.IsNullOrEmpty(settings.NopVatsk) || string.IsNullOrEmpty(settings.NopPokladnica))
                continue;

            var credentials = _configService.GetCredentials(settings);
            if (!credentials.HasNopCertificate)
                continue;

            wanted.Add(settings.StoreId);
            var fingerprint = StoreConnection.Fingerprint(settings, credentials);

            if (_connections.TryGetValue(settings.StoreId, out var existing))
            {
                if (existing.ConfigFingerprint == fingerprint)
                    continue;
                await existing.DisposeAsync();
                _connections.Remove(settings.StoreId);
            }

            var connection = new StoreConnection(this, settings, credentials, fingerprint, _logger);
            _connections[settings.StoreId] = connection;
            connection.Start(ct);
        }

        foreach (var storeId in _connections.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            await _connections[storeId].DisposeAsync();
            _connections.Remove(storeId);
        }
    }

    internal async Task HandleNotificationAsync(SepaStoreSettings settings, string payloadJson, string sourceId, CancellationToken ct)
    {
        var notification = _processor.Parse(payloadJson);
        if (notification is null)
        {
            _logger.LogWarning("nop_notification_unparsable store={StoreId} source={Source}", settings.StoreId, sourceId);
            return;
        }

        var confirmed = _processor.ToConfirmedPayment(notification, settings.Iban);
        if (confirmed is null)
        {
            _logger.LogInformation(
                "nop_notification_ignored store={StoreId} status={Status} e2e={EndToEndId}",
                settings.StoreId, notification.TransactionStatus, notification.EndToEndId);
            return;
        }

        await _matchingService.ProcessAsync(sourceId, confirmed, settings.AmountTolerance, ct);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastCatchUp = new();
    private static readonly TimeSpan CatchUpOverlap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Missed-notification catch-up: NOP retains notifications for 2 hours.
    /// The per-store timestamp advances only after a fully successful pass,
    /// so a failed fetch re-covers the same window next time (dedup keys make
    /// re-processing harmless).
    /// </summary>
    internal async Task CatchUpAsync(SepaStoreSettings settings, SepaBackendCredentials credentials, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var dateFrom = _lastCatchUp.TryGetValue(settings.StoreId, out var last)
            ? last - CatchUpOverlap
            : startedAt.AddHours(-2);

        try
        {
            using var client = NopRestClient.Create(credentials, _logger);
            var json = await client.GetAllTransactionsAsync(
                $"POKLADNICA-{settings.NopPokladnica}",
                dateFrom,
                ct);

            if (json.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var element in json.EnumerateArray())
                    await HandleNotificationAsync(settings, element.GetRawText(), "nop-mqtt:catchup", ct);
            }

            _lastCatchUp[settings.StoreId] = startedAt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NOP catch-up failed for store {StoreId}", settings.StoreId);
        }
    }

    private sealed class StoreConnection : IAsyncDisposable
    {
        private readonly NopMqttListener _owner;
        private readonly SepaStoreSettings _settings;
        private readonly SepaBackendCredentials _credentials;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private CancellationTokenSource? _linkedCts;
        private Task? _runTask;

        public string ConfigFingerprint { get; }

        public StoreConnection(
            NopMqttListener owner,
            SepaStoreSettings settings,
            SepaBackendCredentials credentials,
            string fingerprint,
            ILogger logger)
        {
            _owner = owner;
            _settings = settings;
            _credentials = credentials;
            _logger = logger;
            ConfigFingerprint = fingerprint;
        }

        public static string Fingerprint(SepaStoreSettings settings, SepaBackendCredentials credentials)
            => string.Join("|",
                settings.NopVatsk, settings.NopPokladnica, settings.Iban,
                credentials.NopEnvironment,
                (credentials.NopPfxBase64 ?? credentials.NopCertificatePem ?? "").GetHashCode());

        public void Start(CancellationToken outerCt)
        {
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt, _cts.Token);
            _runTask = Task.Run(() => RunAsync(_linkedCts.Token), CancellationToken.None);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                IMqttClient? client = null;
                try
                {
                    attempt++;
                    var certificate = NopCertificateLoader.Load(_credentials);
                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(NopRestClient.MqttHostFor(_credentials.NopEnvironment), 8883)
                        .WithProtocolVersion(MqttProtocolVersion.V311)
                        .WithClientId($"btcpay-sepa-{_settings.NopPokladnica}")
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                        .WithTlsOptions(o => o
                            .UseTls()
                            .WithClientCertificates([certificate]))
                        .Build();

                    client = new MqttClientFactory().CreateMqttClient();
                    client.ApplicationMessageReceivedAsync += async e =>
                    {
                        try
                        {
                            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                            await _owner.HandleNotificationAsync(_settings, payload, NopMqttSource.BackendId, ct);
                        }
                        catch (Exception ex)
                        {
                            // Never let a message error take the connection down.
                            _logger.LogError(ex, "NOP MQTT message handling failed for store {StoreId}", _settings.StoreId);
                        }
                    };

                    await client.ConnectAsync(options, ct);
                    await client.SubscribeAsync(
                        new MqttTopicFilterBuilder()
                            .WithTopic($"{_settings.NopVatsk}/POKLADNICA-{_settings.NopPokladnica}/#")
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .Build(),
                        ct);

                    _logger.LogInformation(
                        "NOP MQTT connected for store {StoreId} ({Vatsk}/POKLADNICA-{Pokladnica}, {Env})",
                        _settings.StoreId, _settings.NopVatsk, _settings.NopPokladnica, _credentials.NopEnvironment);
                    attempt = 0;

                    await _owner.CatchUpAsync(_settings, _credentials, ct);

                    // Stay connected until dropped or cancelled.
                    while (!ct.IsCancellationRequested && client.IsConnected)
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);

                    if (!ct.IsCancellationRequested)
                        _logger.LogWarning("NOP MQTT connection dropped for store {StoreId}; reconnecting", _settings.StoreId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "NOP MQTT connect attempt {Attempt} failed for store {StoreId}", attempt, _settings.StoreId);
                }
                finally
                {
                    if (client is not null)
                    {
                        try { await client.DisconnectAsync(); } catch { /* already gone */ }
                        client.Dispose();
                    }
                }

                if (ct.IsCancellationRequested)
                    break;

                // Manual's backoff; after the attempt budget, pause and start over.
                var delay = attempt >= NopBackoff.MaxAttempts ? FailurePause : NopBackoff.DelayForAttempt(attempt);
                if (attempt >= NopBackoff.MaxAttempts)
                    attempt = 0;
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_runTask is not null)
            {
                try { await _runTask; } catch { /* run loop owns its errors */ }
            }
            _linkedCts?.Dispose();
            _cts.Dispose();
        }
    }
}
