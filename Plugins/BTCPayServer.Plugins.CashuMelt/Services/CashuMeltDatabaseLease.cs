using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Data.Common;
using BTCPayServer.Plugins.CashuMelt.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Session advisory locks preserve ownership across independent durable saves and HTTP calls.</summary>
internal sealed class CashuMeltDatabaseLease : IAsyncDisposable
{
    private readonly CashuMeltDbContext _context;
    private readonly string _name;
    private readonly bool _shared;
    private int _disposed;
    private CashuMeltDatabaseLease(CashuMeltDbContext context, string name, bool shared)
    { _context = context; _name = name; _shared = shared; }

    public static async Task<CashuMeltDatabaseLease?> TryAcquireAsync(
        CashuMeltDbContextFactory factory, string name, bool shared, CancellationToken ct)
    {
        var context = factory.CreateContext();
        try
        {
            await context.Database.OpenConnectionAsync(ct);
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT pg_try_advisory_lock{(shared ? "_shared" : "")}(hashtextextended(@name, 0))";
            command.Parameters.Add(new NpgsqlParameter("name", name));
            if (await command.ExecuteScalarAsync(ct) is true)
                return new CashuMeltDatabaseLease(context, name, shared);
            await context.DisposeAsync();
            return null;
        }
        catch { await context.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await using var command = _context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT pg_advisory_unlock{(_shared ? "_shared" : "")}(hashtextextended(@name, 0))";
            command.Parameters.Add(new NpgsqlParameter("name", _name));
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch
        {
            // Never return a connection with an unreleased session lock to the pool.
            if (_context.Database.GetDbConnection() is NpgsqlConnection connection) NpgsqlConnection.ClearPool(connection);
            throw;
        }
        finally { await _context.DisposeAsync(); }
    }
}
