using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SepaInstantQr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSkQrVariant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SkQrVariant",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "payme");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkQrVariant",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings");
        }
    }
}
