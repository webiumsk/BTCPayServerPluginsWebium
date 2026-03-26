using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.CashuMelt.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.CashuMelt");

            migrationBuilder.CreateTable(
                name: "CashuMeltStoreSettings",
                schema: "BTCPayServer.Plugins.CashuMelt",
                columns: table => new
                {
                    StoreId = table.Column<string>(maxLength: 100, nullable: false),
                    MintUrl = table.Column<string>(maxLength: 500, nullable: false),
                    Unit = table.Column<string>(maxLength: 20, nullable: true),
                    LightningAddress = table.Column<string>(maxLength: 500, nullable: true),
                    Enabled = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashuMeltStoreSettings", x => x.StoreId);
                });

            migrationBuilder.CreateTable(
                name: "CashuMeltPaymentRequests",
                schema: "BTCPayServer.Plugins.CashuMelt",
                columns: table => new
                {
                    QuoteId = table.Column<string>(maxLength: 100, nullable: false),
                    InvoiceId = table.Column<string>(maxLength: 100, nullable: false),
                    StoreId = table.Column<string>(maxLength: 100, nullable: false),
                    AmountSats = table.Column<long>(nullable: false),
                    Unit = table.Column<string>(maxLength: 20, nullable: true),
                    Bolt11Invoice = table.Column<string>(maxLength: 500, nullable: true),
                    State = table.Column<string>(maxLength: 50, nullable: true),
                    SettlementState = table.Column<string>(maxLength: 50, nullable: true),
                    SettlementError = table.Column<string>(maxLength: 500, nullable: true),
                    SettlementReference = table.Column<string>(maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashuMeltPaymentRequests", x => x.QuoteId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashuMeltPaymentRequests_InvoiceId",
                schema: "BTCPayServer.Plugins.CashuMelt",
                table: "CashuMeltPaymentRequests",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CashuMeltPaymentRequests_StoreId",
                schema: "BTCPayServer.Plugins.CashuMelt",
                table: "CashuMeltPaymentRequests",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashuMeltStoreSettings",
                schema: "BTCPayServer.Plugins.CashuMelt");

            migrationBuilder.DropTable(
                name: "CashuMeltPaymentRequests",
                schema: "BTCPayServer.Plugins.CashuMelt");
        }
    }
}
