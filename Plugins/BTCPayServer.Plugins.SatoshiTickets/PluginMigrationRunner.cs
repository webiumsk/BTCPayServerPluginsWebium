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
        await using var ctx = _pluginDbContextFactory.CreateContext();
        var pending = (await ctx.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
            _logger.LogInformation("Satoshi Tickets: applying {Count} pending migration(s): {Migrations}",
                pending.Count, string.Join(", ", pending));

        await ctx.Database.MigrateAsync(cancellationToken);

        if (pending.Count > 0)
            _logger.LogInformation("Satoshi Tickets: database migrations complete");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

