#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.BTCPayRaffle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BTCPayServer.Plugins.BTCPayRaffle;

public class PluginMigrationRunner : IStartupTask
{
    private readonly RaffleDbContextFactory _dbContextFactory;
    private readonly ILogger<PluginMigrationRunner> _logger;

    public PluginMigrationRunner(
        RaffleDbContextFactory dbContextFactory,
        ILogger<PluginMigrationRunner> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Running BTCPayRaffle database migrations");
        await using var ctx = _dbContextFactory.CreateContext();

        try
        {
            var pending = await ctx.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pending.Any())
            {
                _logger.LogInformation("Applying {Count} BTCPayRaffle migration(s)", pending.Count());
                await ctx.Database.MigrateAsync(cancellationToken);
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            _logger.LogWarning(ex,
                "BTCPayRaffle EF migrations failed; falling back to raw-SQL schema creator");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BTCPayRaffle EF migrations encountered an unexpected error; attempting schema creator fallback");
        }

        // Idempotent creator ensures all tables exist regardless of migration path
        await RaffleSchemaCreator.EnsureSchemaAndTablesAsync(ctx, cancellationToken);

        _logger.LogInformation("BTCPayRaffle database ready");
    }
}
