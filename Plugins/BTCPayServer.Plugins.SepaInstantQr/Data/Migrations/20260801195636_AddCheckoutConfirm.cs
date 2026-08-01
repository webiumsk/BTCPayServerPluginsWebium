using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SepaInstantQr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckoutConfirm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CheckoutConfirmEnabled",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckoutConfirmEnabled",
                schema: "BTCPayServer.Plugins.SepaInstantQr",
                table: "SepaStoreSettings");
        }
    }
}
