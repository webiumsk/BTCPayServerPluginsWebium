#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BTCPayServer.Plugins.SepaInstantQr;

public class PluginMigrationRunner : IStartupTask
{
    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<PluginMigrationRunner> _logger;

    public PluginMigrationRunner(
        SepaDbContextFactory dbContextFactory,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ILogger<PluginMigrationRunner> logger)
    {
        _dbContextFactory = dbContextFactory;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // A migration failure here must never take the whole server down (a
        // startup-task exception aborts the host and BTCPay then disables the
        // plugin on the next boot). A transient DB hiccup during a restart is
        // survivable: every runtime query site tolerates missing tables, so we
        // log and let the server come up - migrations retry on the next boot.
        try
        {
            _logger.LogInformation("Migrating SepaInstantQr plugin database");
            await using var ctx = _dbContextFactory.CreateContext();

            var pending = await ctx.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pending.Any())
            {
                _logger.LogInformation("Applying {Count} SepaInstantQr migration(s)", pending.Count());
                await ctx.Database.MigrateAsync(cancellationToken);
            }

            await EnsurePaymentMethodRegisteredAsync(ctx, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Do NOT rethrow: BTCPay attributes any startup exception whose
            // stack trace crosses a plugin assembly to that plugin and
            // disables it (Program.Main + PluginManager.IsExceptionByPlugin) -
            // even when the cancellation came from the host itself.
            _logger.LogInformation("SepaInstantQr migration cancelled by host shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SepaInstantQr database migration failed; the plugin may be degraded until the next restart.");
        }
    }

    /// <summary>
    /// Every store with enabled plugin settings must also carry the
    /// SEPA_INSTANT payment method config in BTCPay's store blob, otherwise
    /// invoice creation never offers the method.
    /// </summary>
    private async Task EnsurePaymentMethodRegisteredAsync(SepaDbContext ctx, CancellationToken ct)
    {
        if (!_handlers.Support(SepaInstantQrPlugin.SepaPaymentMethodId))
            return;

        Data.Entities.SepaStoreSettings[] allSettings;
        try
        {
            allSettings = await ctx.SepaStoreSettings.ToArrayAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning(ex, "SepaStoreSettings table not ready; skipping payment method registration.");
            return;
        }

        var handler = _handlers[SepaInstantQrPlugin.SepaPaymentMethodId];
        foreach (var settings in allSettings)
        {
            var store = await _storeRepository.FindStore(settings.StoreId);
            if (store is null)
                continue;

            var existing = store.GetPaymentMethodConfig(SepaInstantQrPlugin.SepaPaymentMethodId);
            if (existing is not null)
                continue;

            store.SetPaymentMethodConfig(handler, new SepaPaymentMethodConfig { Enabled = settings.Enabled });
            await _storeRepository.UpdateStore(store);
            _logger.LogInformation("Registered SEPA_INSTANT payment method for store {StoreId}", settings.StoreId);
        }
    }
}
