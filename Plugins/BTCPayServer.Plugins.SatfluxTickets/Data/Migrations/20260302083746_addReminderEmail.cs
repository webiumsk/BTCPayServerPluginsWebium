using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SatfluxTickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class addReminderEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReminderEmailBody",
                schema: "BTCPayServer.Plugins.SatfluxTickets",
                table: "SatfluxTicketsSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReminderEmailSubject",
                schema: "BTCPayServer.Plugins.SatfluxTickets",
                table: "SatfluxTicketsSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReminderEmailBody",
                schema: "BTCPayServer.Plugins.SatfluxTickets",
                table: "SatfluxTicketsSettings");

            migrationBuilder.DropColumn(
                name: "ReminderEmailSubject",
                schema: "BTCPayServer.Plugins.SatfluxTickets",
                table: "SatfluxTicketsSettings");
        }
    }
}
