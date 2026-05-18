using BTCPayServer.Plugins.CashuMelt.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.CashuMelt.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(CashuMeltDbContext))]
[Migration("20260424120000_AddMerchantRiskControls")]
public partial class AddMerchantRiskControls : Migration
{
    private const string Schema = "BTCPayServer.Plugins.CashuMelt";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TrustedMintUrls",
            schema: Schema,
            table: "CashuMeltStoreSettings",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "MaxMeltFeeReserveSats",
            schema: Schema,
            table: "CashuMeltStoreSettings",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "MaxMeltFeeReservePercentOfMinted",
            schema: Schema,
            table: "CashuMeltStoreSettings",
            type: "numeric(5,2)",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_CashuMeltPaymentRequests_SettlementState",
            schema: Schema,
            table: "CashuMeltPaymentRequests",
            column: "SettlementState");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CashuMeltPaymentRequests_SettlementState",
            schema: Schema,
            table: "CashuMeltPaymentRequests");

        migrationBuilder.DropColumn(name: "TrustedMintUrls", schema: Schema, table: "CashuMeltStoreSettings");
        migrationBuilder.DropColumn(name: "MaxMeltFeeReserveSats", schema: Schema, table: "CashuMeltStoreSettings");
        migrationBuilder.DropColumn(name: "MaxMeltFeeReservePercentOfMinted", schema: Schema, table: "CashuMeltStoreSettings");
    }
}
