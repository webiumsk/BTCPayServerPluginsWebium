using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.CashuMelt.Data;

/// <summary>
/// Creates CashuMelt plugin schema and tables via raw SQL when migrations fail (e.g. in Docker/custom DB setups).
/// </summary>
public static class CashuMeltSchemaCreator
{
    public static async Task EnsureSchemaAndTablesAsync(CashuMeltDbContext ctx, CancellationToken cancellationToken = default)
    {
        const string schema = "BTCPayServer.Plugins.CashuMelt";
        // Use quoted identifiers for schema/table names with dots
        await ctx.Database.ExecuteSqlRawAsync($@"CREATE SCHEMA IF NOT EXISTS ""{schema}""", cancellationToken);

        await ctx.Database.ExecuteSqlRawAsync($@"
            CREATE TABLE IF NOT EXISTS ""{schema}"".""CashuMeltStoreSettings"" (
                ""StoreId"" varchar(100) NOT NULL PRIMARY KEY,
                ""MintUrl"" varchar(500) NOT NULL,
                ""Unit"" varchar(20),
                ""LightningAddress"" varchar(500),
                ""Enabled"" boolean NOT NULL,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NOT NULL
            )", cancellationToken);

        await ctx.Database.ExecuteSqlRawAsync($@"
            CREATE TABLE IF NOT EXISTS ""{schema}"".""CashuMeltPaymentRequests"" (
                ""QuoteId"" varchar(100) NOT NULL PRIMARY KEY,
                ""InvoiceId"" varchar(100) NOT NULL,
                ""StoreId"" varchar(100) NOT NULL,
                ""AmountSats"" bigint NOT NULL,
                ""Unit"" varchar(20),
                ""Bolt11Invoice"" varchar(500),
                ""State"" varchar(50),
                ""SettlementState"" varchar(50),
                ""SettlementError"" varchar(500),
                ""SettlementReference"" varchar(200),
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""PaidAt"" timestamp with time zone,
                ""SettledAt"" timestamp with time zone
            )", cancellationToken);

        await ctx.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""{schema}"".""CashuMeltPaymentRequests"" ADD COLUMN IF NOT EXISTS ""SettlementState"" varchar(50)", cancellationToken);
        await ctx.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""{schema}"".""CashuMeltPaymentRequests"" ADD COLUMN IF NOT EXISTS ""SettlementError"" varchar(500)", cancellationToken);
        await ctx.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""{schema}"".""CashuMeltPaymentRequests"" ADD COLUMN IF NOT EXISTS ""SettlementReference"" varchar(200)", cancellationToken);
        await ctx.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""{schema}"".""CashuMeltPaymentRequests"" ADD COLUMN IF NOT EXISTS ""SettledAt"" timestamp with time zone", cancellationToken);
        await ctx.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""{schema}"".""CashuMeltPaymentRequests"" ADD COLUMN IF NOT EXISTS ""MintedProofsJson"" text", cancellationToken);
        await ctx.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""{schema}"".""CashuMeltPaymentRequests"" ADD COLUMN IF NOT EXISTS ""MeltQuoteId"" varchar(200)", cancellationToken);
        await ctx.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""{schema}"".""CashuMeltPaymentRequests"" ADD COLUMN IF NOT EXISTS ""ForwardBolt11"" text", cancellationToken);

        // Create indexes if they don't exist (PostgreSQL: CREATE INDEX IF NOT EXISTS)
        await ctx.Database.ExecuteSqlRawAsync($@"
            CREATE INDEX IF NOT EXISTS ""IX_CashuMeltPaymentRequests_InvoiceId"" 
            ON ""{schema}"".""CashuMeltPaymentRequests"" (""InvoiceId"")", cancellationToken);
        await ctx.Database.ExecuteSqlRawAsync($@"
            CREATE INDEX IF NOT EXISTS ""IX_CashuMeltPaymentRequests_StoreId"" 
            ON ""{schema}"".""CashuMeltPaymentRequests"" (""StoreId"")", cancellationToken);
    }
}
