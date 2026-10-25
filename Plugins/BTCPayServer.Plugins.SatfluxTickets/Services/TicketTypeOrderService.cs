using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>Serializes membership and ordering changes across every merchant UI.</summary>
public sealed class TicketTypeOrderService(SimpleTicketSalesDbContextFactory factory)
{
    // Do not replay a user-managed transaction after an uncertain commit. The revision makes
    // an explicit client reload safe; BTCPay's default retry strategy rejects manual transactions.
    public static SimpleTicketSalesDbContext CreateContext(SimpleTicketSalesDbContextFactory factory) =>
        factory.CreateContext(options => options.ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies)));

    public static IQueryable<TicketType> InDisplayOrder(IQueryable<TicketType> query) =>
        query.OrderBy(t => t.DisplayOrder).ThenBy(t => t.Id);

    // The caller owns a transaction. PostgreSQL row locks also coordinate separate app instances.
    public static async Task<Event> LockEventAsync(SimpleTicketSalesDbContext ctx, string storeId, string eventId,
        CancellationToken cancellationToken = default)
    {
        if (ctx.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An event lock requires a transaction.");
        var rows = await ctx.Events.FromSqlInterpolated($"""
            SELECT * FROM "BTCPayServer.Plugins.SatfluxTickets"."Events"
            WHERE "Id" = {eventId} AND "StoreId" = {storeId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public async Task<TicketTypeOrderSnapshot> GetAsync(string storeId, string eventId, CancellationToken cancellationToken = default)
    {
        await using var ctx = CreateContext(factory);
        await using var transaction = await ctx.Database.BeginTransactionAsync(cancellationToken);
        var ticketEvent = await LockEventAsync(ctx, storeId, eventId, cancellationToken);
        if (ticketEvent is null) return null;
        var result = await SnapshotAsync(ctx, ticketEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TicketTypeOrderSnapshot> SetAsync(string storeId, string eventId, TicketTypeOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request)) throw new ArgumentException("Invalid ticket type order.");
        await using var ctx = CreateContext(factory);
        await using var transaction = await ctx.Database.BeginTransactionAsync(cancellationToken);
        var ticketEvent = await LockEventAsync(ctx, storeId, eventId, cancellationToken);
        if (ticketEvent is null) return null;
        // Only ranks are changed. Tracked entities must never overwrite sales counters.
        var types = await ctx.TicketTypes.Where(t => t.EventId == eventId).ToListAsync(cancellationToken);
        if (ticketEvent.TicketTypeOrderRevision != request.ExpectedRevision ||
            !IsCompletePermutation(request.TicketTypeIds, types.Select(t => t.Id).ToArray()))
            throw new TicketTypeOrderConflictException();
        var byId = types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        for (var index = 0; index < request.TicketTypeIds.Length; index++)
            byId[request.TicketTypeIds[index]].DisplayOrder = index;
        ticketEvent.TicketTypeOrderRevision = checked(ticketEvent.TicketTypeOrderRevision + 1);
        await ctx.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TicketTypeOrderSnapshot { TicketTypeIds = request.TicketTypeIds, Revision = ticketEvent.TicketTypeOrderRevision };
    }

    public static bool IsValidRequest(TicketTypeOrderRequest request) => request is not null &&
        request.ExpectedRevision is >= 0 and <= 9007199254740991L && request.TicketTypeIds is { Length: <= 1000 } &&
        request.TicketTypeIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128) &&
        request.TicketTypeIds.Distinct(StringComparer.Ordinal).Count() == request.TicketTypeIds.Length;

    public static bool IsCompletePermutation(string[] requested, string[] existing) =>
        requested is not null && existing is not null && requested.Length == existing.Length &&
        requested.Distinct(StringComparer.Ordinal).Count() == requested.Length &&
        requested.ToHashSet(StringComparer.Ordinal).SetEquals(existing);

    private static async Task<TicketTypeOrderSnapshot> SnapshotAsync(SimpleTicketSalesDbContext ctx, Event ticketEvent,
        CancellationToken cancellationToken) => new()
    {
        Revision = ticketEvent.TicketTypeOrderRevision,
        TicketTypeIds = await InDisplayOrder(ctx.TicketTypes.Where(t => t.EventId == ticketEvent.Id))
            .Select(t => t.Id).ToArrayAsync(cancellationToken)
    };
}

public sealed class TicketTypeOrderConflictException : Exception { }
