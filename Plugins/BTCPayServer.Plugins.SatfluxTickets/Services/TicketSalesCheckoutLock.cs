using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>Serialize a server-issued checkout across workers without holding a DB transaction across invoice HTTP.</summary>
internal sealed class TicketSalesCheckoutLock : IAsyncDisposable
{
    private readonly SimpleTicketSalesDbContext _context;
    private readonly long _key;
    private TicketSalesCheckoutLock(SimpleTicketSalesDbContext context, long key) { _context = context; _key = key; }

    internal static async Task<TicketSalesCheckoutLock> AcquireAsync(SimpleTicketSalesDbContext context,
        string storeId, string eventId, string txnId, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"SatfluxTickets:checkout:{storeId}:{eventId}:{txnId}"));
        var key = BinaryPrimitives.ReadInt64BigEndian(hash);
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({key})", cancellationToken);
            return new TicketSalesCheckoutLock(context, key);
        }
        catch
        {
            await context.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({_key})", CancellationToken.None); }
        finally { await _context.Database.CloseConnectionAsync(); }
    }
}
