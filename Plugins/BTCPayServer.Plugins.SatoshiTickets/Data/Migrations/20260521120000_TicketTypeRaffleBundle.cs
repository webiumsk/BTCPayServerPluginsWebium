using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SatoshiTickets.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketTypeRaffleBundle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BundledRaffleId",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "TicketTypes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BundledRaffleTicketsPerAdmission",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "TicketTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "BTCPayServer.Plugins.SatoshiTickets"."TicketTypes" tt
                SET "BundledRaffleId" = e."BundledRaffleId",
                    "BundledRaffleTicketsPerAdmission" = e."BundledRaffleTicketsPerAdmission"
                FROM "BTCPayServer.Plugins.SatoshiTickets"."Events" e
                WHERE tt."EventId" = e."Id"
                  AND e."BundledRaffleTicketsPerAdmission" > 0
                  AND e."BundledRaffleId" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "BundledRaffleId",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "BundledRaffleTicketsPerAdmission",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "Events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.Sql("""
                UPDATE "BTCPayServer.Plugins.SatoshiTickets"."Events" e
                SET "BundledRaffleId" = sub."BundledRaffleId",
                    "BundledRaffleTicketsPerAdmission" = sub."BundledRaffleTicketsPerAdmission"
                FROM (
                    SELECT DISTINCT ON (tt."EventId")
                        tt."EventId",
                        tt."BundledRaffleId",
                        tt."BundledRaffleTicketsPerAdmission"
                    FROM "BTCPayServer.Plugins.SatoshiTickets"."TicketTypes" tt
                    WHERE tt."BundledRaffleTicketsPerAdmission" > 0
                      AND tt."BundledRaffleId" IS NOT NULL
                    ORDER BY tt."EventId", tt."IsDefault" DESC, tt."Name"
                ) sub
                WHERE e."Id" = sub."EventId";
                """);

            migrationBuilder.DropColumn(
                name: "BundledRaffleId",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "TicketTypes");

            migrationBuilder.DropColumn(
                name: "BundledRaffleTicketsPerAdmission",
                schema: "BTCPayServer.Plugins.SatoshiTickets",
                table: "TicketTypes");
        }
    }
}
