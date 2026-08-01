using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SepaInstantQr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFioTokenFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FioTokenFingerprint",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SepaStoreSettings_FioTokenFingerprint",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings",
                column: "FioTokenFingerprint",
                unique: true,
                filter: "\"FioTokenFingerprint\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SepaStoreSettings_FioTokenFingerprint",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings");

            migrationBuilder.DropColumn(
                name: "FioTokenFingerprint",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings");
        }
    }
}
