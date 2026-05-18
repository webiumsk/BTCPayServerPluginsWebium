using BTCPayServer.Plugins.CashuMelt.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.CashuMelt.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(CashuMeltDbContext))]
[Migration("20260517000000_AddRetryTracking")]
public partial class AddRetryTracking : Migration
{
    private const string Schema = "BTCPayServer.Plugins.CashuMelt";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "RetryCount",
            schema: Schema,
            table: "CashuMeltPaymentRequests",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "NeedsManualReview",
            schema: Schema,
            table: "CashuMeltPaymentRequests",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "FailureReasonCode",
            schema: Schema,
            table: "CashuMeltPaymentRequests",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        // Partial index for quick lookup of rows needing review
        migrationBuilder.Sql(
            $"""
            CREATE INDEX "IX_CashuMeltPaymentRequests_NeedsManualReview"
            ON "{Schema}"."CashuMeltPaymentRequests" ("NeedsManualReview")
            WHERE "NeedsManualReview" = true;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CashuMeltPaymentRequests_NeedsManualReview",
            schema: Schema,
            table: "CashuMeltPaymentRequests");

        migrationBuilder.DropColumn(name: "RetryCount", schema: Schema, table: "CashuMeltPaymentRequests");
        migrationBuilder.DropColumn(name: "NeedsManualReview", schema: Schema, table: "CashuMeltPaymentRequests");
        migrationBuilder.DropColumn(name: "FailureReasonCode", schema: Schema, table: "CashuMeltPaymentRequests");
    }
}
