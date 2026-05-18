#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.BTCPayRaffle.Data;

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
    ""Id""              uuid           NOT NULL PRIMARY KEY,
    ""Name""            text           NOT NULL,
    ""Description""     text,
    ""StoreId""         text           NOT NULL,
    ""TicketPriceSats"" bigint         NOT NULL,
    ""TicketCurrency""  varchar(10)    NOT NULL DEFAULT 'SATS',
    ""TicketPrice""     numeric(18,8)  NOT NULL DEFAULT 0,
    ""MaxTickets""      integer,
    ""Status""          integer        NOT NULL DEFAULT 0,
    ""CreatedAt""       timestamptz    NOT NULL,
    ""OpenedAt""        timestamptz,
    ""ClosedAt""        timestamptz,
    ""CompletedAt""     timestamptz
);
CREATE INDEX IF NOT EXISTS ""IX_Raffles_StoreId""
    ON ""{schema}"".""Raffles"" (""StoreId"");

CREATE TABLE IF NOT EXISTS ""{schema}"".""RaffleTickets"" (
    ""Id""           uuid        NOT NULL PRIMARY KEY,
    ""RaffleId""     uuid        NOT NULL REFERENCES ""{schema}"".""Raffles""(""Id"") ON DELETE CASCADE,
    ""TicketNumber"" integer     NOT NULL,
    ""InvoiceId""    varchar(100) NOT NULL,
    ""IsManual""     boolean     NOT NULL DEFAULT false,
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

        await ctx.Database.ExecuteSqlRawAsync($@"
ALTER TABLE ""{schema}"".""Raffles""
    ADD COLUMN IF NOT EXISTS ""TicketCurrency"" varchar(10) NOT NULL DEFAULT 'SATS';
ALTER TABLE ""{schema}"".""Raffles""
    ADD COLUMN IF NOT EXISTS ""TicketPrice"" numeric(18,8) NOT NULL DEFAULT 0;
UPDATE ""{schema}"".""Raffles""
    SET ""TicketCurrency"" = 'SATS', ""TicketPrice"" = ""TicketPriceSats""
    WHERE ""TicketPrice"" = 0;
ALTER TABLE ""{schema}"".""RaffleTickets""
    ADD COLUMN IF NOT EXISTS ""IsManual"" boolean NOT NULL DEFAULT false;
", ct);
    }
}
