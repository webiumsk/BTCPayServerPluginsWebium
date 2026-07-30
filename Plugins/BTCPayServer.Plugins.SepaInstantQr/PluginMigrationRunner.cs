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
