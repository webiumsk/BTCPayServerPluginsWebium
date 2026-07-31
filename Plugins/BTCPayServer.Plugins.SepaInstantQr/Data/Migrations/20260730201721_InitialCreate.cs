using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SepaInstantQr.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.SepaInstantQr");

            migrationBuilder.CreateTable(
                name: "SepaPaymentRequests",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                columns: table => new
                {
                    Reference = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    ReferenceKind = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    InvoiceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StoreId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Backend = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AmountDue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    QrPayload = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RawConfirmationJson = table.Column<string>(type: "text", nullable: true),
                    ReviewReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DedupKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SepaPaymentRequests", x => x.Reference);
                });

            migrationBuilder.CreateTable(
                name: "SepaStoreSettings",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                columns: table => new
                {
                    StoreId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CountryProfile = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    Beneficiary = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: false),
                    Bic = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    Message = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ConfirmationBackend = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AmountTolerance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EncryptedCredentialsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SepaStoreSettings", x => x.StoreId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SepaPaymentRequests_DedupKey",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaPaymentRequests",
                column: "DedupKey",
                unique: true,
                filter: "\"DedupKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SepaPaymentRequests_InvoiceId",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaPaymentRequests",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SepaPaymentRequests_State",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaPaymentRequests",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_SepaPaymentRequests_StoreId",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaPaymentRequests",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SepaPaymentRequests",
                schema: "BTCPayServer.Plugins.SepaInstantQr");

            migrationBuilder.DropTable(
                name: "SepaStoreSettings",
                schema: "BTCPayServer.Plugins.SepaInstantQr");
        }
    }
}
