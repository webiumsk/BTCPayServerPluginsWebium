using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SepaInstantQr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNopIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NopPokladnica",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NopVatsk",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NopPokladnica",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings");

            migrationBuilder.DropColumn(
                name: "NopVatsk",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings");
        }
    }
}
