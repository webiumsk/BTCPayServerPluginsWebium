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
CREATE INDEX IF NOT EXISTS ""IX_RaffleTickets_RaffleId_BuyerEmail""
    ON ""{schema}"".""RaffleTickets"" (""RaffleId"", ""BuyerEmail"");

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

        // Older fallback schemas can predate the current accepted payload widths.
        // Never narrow an operator's wider/unbounded column or replace a custom domain.
        // Check again after locking so a concurrent widening cannot be undone.
        // Explicitly retain catalog collation; ALTER TYPE alone can reset it.
        await ctx.Database.ExecuteSqlRawAsync("""
DO $raffle_width_alignment$
DECLARE
    target_collation_schema name;
    target_collation_name name;
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'BTCPayServer.Plugins.BTCPayRaffle'
          AND table_name = 'Raffles' AND column_name = 'Description'
          AND data_type = 'character varying' AND domain_name IS NULL
          AND character_maximum_length < 8000
    ) THEN
        LOCK TABLE "BTCPayServer.Plugins.BTCPayRaffle"."Raffles" IN ACCESS EXCLUSIVE MODE;
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'BTCPayServer.Plugins.BTCPayRaffle'
              AND table_name = 'Raffles' AND column_name = 'Description'
              AND data_type = 'character varying' AND domain_name IS NULL
              AND character_maximum_length < 8000
        ) THEN
            SELECT n.nspname, c.collname INTO STRICT target_collation_schema, target_collation_name
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_collation c ON c.oid = a.attcollation
            JOIN pg_catalog.pg_namespace n ON n.oid = c.collnamespace
            WHERE a.attrelid = '"BTCPayServer.Plugins.BTCPayRaffle"."Raffles"'::regclass
              AND a.attname = 'Description' AND a.attnum > 0 AND NOT a.attisdropped;
            EXECUTE format(
                'ALTER TABLE "BTCPayServer.Plugins.BTCPayRaffle"."Raffles" ALTER COLUMN "Description" TYPE text COLLATE %I.%I',
                target_collation_schema, target_collation_name);
        END IF;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'BTCPayServer.Plugins.BTCPayRaffle'
          AND table_name = 'RaffleTickets' AND column_name = 'InvoiceId'
          AND data_type = 'character varying' AND domain_name IS NULL
          AND character_maximum_length < 100
    ) THEN
        LOCK TABLE "BTCPayServer.Plugins.BTCPayRaffle"."RaffleTickets" IN ACCESS EXCLUSIVE MODE;
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'BTCPayServer.Plugins.BTCPayRaffle'
              AND table_name = 'RaffleTickets' AND column_name = 'InvoiceId'
              AND data_type = 'character varying' AND domain_name IS NULL
              AND character_maximum_length < 100
        ) THEN
            SELECT n.nspname, c.collname INTO STRICT target_collation_schema, target_collation_name
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_collation c ON c.oid = a.attcollation
            JOIN pg_catalog.pg_namespace n ON n.oid = c.collnamespace
            WHERE a.attrelid = '"BTCPayServer.Plugins.BTCPayRaffle"."RaffleTickets"'::regclass
              AND a.attname = 'InvoiceId' AND a.attnum > 0 AND NOT a.attisdropped;
            EXECUTE format(
                'ALTER TABLE "BTCPayServer.Plugins.BTCPayRaffle"."RaffleTickets" ALTER COLUMN "InvoiceId" TYPE character varying(100) COLLATE %I.%I',
                target_collation_schema, target_collation_name);
        END IF;
    END IF;
END;
$raffle_width_alignment$;
""", ct);
    }
}
