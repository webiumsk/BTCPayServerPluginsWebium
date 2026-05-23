using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SatoshiTickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventRaffleBundle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BundledRaffleId",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "Events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BundledRaffleTicketsPerAdmission",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "Events",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BundledRaffleId",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "BundledRaffleTicketsPerAdmission",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "Events");
        }
    }
}
