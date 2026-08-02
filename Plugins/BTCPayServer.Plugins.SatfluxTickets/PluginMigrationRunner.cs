using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SatfluxTickets;

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

            // 2.0.0 rename cutover: existing installations carry the data in
            // the pre-rename schema. Renaming the schema moves every table
            // INCLUDING the EF migration history, so applied migrations stay
            // applied. Idempotent - no-op on fresh installs and re-runs.
            await ctx.Database.ExecuteSqlRawAsync("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'BTCPayServer.Plugins.SatoshiTickets')
                       AND NOT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'BTCPayServer.Plugins.SatfluxTickets')
                    THEN
                        ALTER SCHEMA "BTCPayServer.Plugins.SatoshiTickets" RENAME TO "BTCPayServer.Plugins.SatfluxTickets";
                    END IF;

                    -- The settings table is the only one whose NAME carried the
                    -- plugin name; the migration that created it is already
                    -- recorded as applied, so it must be renamed here too.
                    IF EXISTS (SELECT 1 FROM information_schema.tables
                               WHERE table_schema = 'BTCPayServer.Plugins.SatfluxTickets' AND table_name = 'SatoshiTicketsSettings')
                       AND NOT EXISTS (SELECT 1 FROM information_schema.tables
                               WHERE table_schema = 'BTCPayServer.Plugins.SatfluxTickets' AND table_name = 'SatfluxTicketsSettings')
                    THEN
                        ALTER TABLE "BTCPayServer.Plugins.SatfluxTickets"."SatoshiTicketsSettings" RENAME TO "SatfluxTicketsSettings";
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_constraint c
                               JOIN pg_class t ON c.conrelid = t.oid
                               JOIN pg_namespace n ON t.relnamespace = n.oid
                               WHERE n.nspname = 'BTCPayServer.Plugins.SatfluxTickets'
                                 AND t.relname = 'SatfluxTicketsSettings'
                                 AND c.conname = 'PK_SatoshiTicketsSettings')
                    THEN
                        ALTER TABLE "BTCPayServer.Plugins.SatfluxTickets"."SatfluxTicketsSettings"
                            RENAME CONSTRAINT "PK_SatoshiTicketsSettings" TO "PK_SatfluxTicketsSettings";
                    END IF;
                END
                $$;
                """, cancellationToken);

            var pending = (await ctx.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
                _logger.LogInformation("Satflux Tickets: applying {Count} pending migration(s): {Migrations}",
                    pending.Count, string.Join(", ", pending));

            await ctx.Database.MigrateAsync(cancellationToken);

            if (pending.Count > 0)
                _logger.LogInformation("Satflux Tickets: database migrations complete");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Satflux Tickets: migration cancelled by host shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Satflux Tickets: database migration failed; the plugin may be degraded until the next restart.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

