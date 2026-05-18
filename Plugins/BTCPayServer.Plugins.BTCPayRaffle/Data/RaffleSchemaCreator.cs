#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.BTCPayRaffle.Data;

/// <summary>
/// Idempotent raw-SQL schema creator used as a fallback when EF Core migrations fail
/// (e.g. on a fresh database that has not yet run migrations, or after manual intervention).
/// Safe to run repeatedly — all statements use IF NOT EXISTS guards.
/// </summary>
public static class RaffleSchemaCreator
{
    public static async Task EnsureSchemaAndTablesAsync(
        RaffleDbContext ctx, CancellationToken ct = default)
    {
        var schema = RaffleDbContextFactory.Schema;

        await ctx.Database.ExecuteSqlRawAsync(
            $@"CREATE SCHEMA IF NOT EXISTS ""{schema}""", ct);

        await ctx.Database.ExecuteSqlRawAsync($@"
CREATE TABLE IF NOT EXISTS ""{schema}"".""Raffles"" (
    ""Id""           uuid        NOT NULL PRIMARY KEY,
    ""Name""         text        NOT NULL,
    ""Description""  text,
    ""StoreId""      text        NOT NULL,
    ""TicketPriceSats"" bigint   NOT NULL,
    ""MaxTickets""   integer,
    ""Status""       integer     NOT NULL DEFAULT 0,
    ""CreatedAt""    timestamptz NOT NULL,
    ""OpenedAt""     timestamptz,
    ""ClosedAt""     timestamptz,
    ""CompletedAt""  timestamptz
);
CREATE INDEX IF NOT EXISTS ""IX_Raffles_StoreId""
    ON ""{schema}"".""Raffles"" (""StoreId"");

CREATE TABLE IF NOT EXISTS ""{schema}"".""RaffleTickets"" (
    ""Id""           uuid        NOT NULL PRIMARY KEY,
    ""RaffleId""     uuid        NOT NULL REFERENCES ""{schema}"".""Raffles""(""Id"") ON DELETE CASCADE,
    ""TicketNumber"" integer     NOT NULL,
    ""InvoiceId""    text        NOT NULL,
    ""BuyerEmail""   text,
    ""BuyerName""    text,
    ""AllocatedAt""  timestamptz NOT NULL,
    UNIQUE (""RaffleId"", ""TicketNumber"")
);
CREATE INDEX IF NOT EXISTS ""IX_RaffleTickets_InvoiceId""
    ON ""{schema}"".""RaffleTickets"" (""InvoiceId"");

CREATE TABLE IF NOT EXISTS ""{schema}"".""RaffleDrawings"" (
    ""Id""              uuid        NOT NULL PRIMARY KEY,
    ""RaffleId""        uuid        NOT NULL REFERENCES ""{schema}"".""Raffles""(""Id"") ON DELETE CASCADE,
    ""DrawOrder""       integer     NOT NULL,
    ""WinningTicketId"" uuid        NOT NULL REFERENCES ""{schema}"".""RaffleTickets""(""Id""),
    ""DrawnAt""         timestamptz NOT NULL,
    UNIQUE (""RaffleId"", ""DrawOrder"")
);
", ct);
    }
}
