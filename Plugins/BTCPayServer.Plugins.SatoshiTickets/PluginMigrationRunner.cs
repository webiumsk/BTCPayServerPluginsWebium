using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SatoshiTickets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SatoshiTickets;

public class PluginMigrationRunner : IHostedService
{
    private readonly SimpleTicketSalesDbContextFactory _pluginDbContextFactory;
    private readonly ILogger<PluginMigrationRunner> _logger;

    public PluginMigrationRunner(
        SimpleTicketSalesDbContextFactory pluginDbContextFactory,
        ILogger<PluginMigrationRunner> logger)
    {
        _pluginDbContextFactory = pluginDbContextFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // A startup exception whose stack trace crosses a plugin assembly gets
        // the plugin auto-disabled by BTCPay (Program.Main + PluginManager.
        // IsExceptionByPlugin) - including an OperationCanceledException caused
        // by the host cancelling its own startup, not by this plugin. Never let
        // a transient DB hiccup take the server down or get us disabled: log
        // and continue - migrations retry on the next boot.
        try
        {
            await using var ctx = _pluginDbContextFactory.CreateContext();
            var pending = (await ctx.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
                _logger.LogInformation("Satoshi Tickets: applying {Count} pending migration(s): {Migrations}",
                    pending.Count, string.Join(", ", pending));

            await ctx.Database.MigrateAsync(cancellationToken);

            if (pending.Count > 0)
                _logger.LogInformation("Satoshi Tickets: database migrations complete");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Satoshi Tickets: migration cancelled by host shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Satoshi Tickets: database migration failed; the plugin may be degraded until the next restart.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

