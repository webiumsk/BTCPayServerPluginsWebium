using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SatfluxTickets.Data.Migrations;

public partial class TicketTypeDisplayOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("DisplayOrder", "TicketTypes", "integer",
            schema: "BTCPayServer.Plugins.SatfluxTickets", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<long>("TicketTypeOrderRevision", "Events", "bigint",
            schema: "BTCPayServer.Plugins.SatfluxTickets", nullable: false, defaultValue: 0L);
        // There is no historical creation timestamp; use the previous administration default.
        migrationBuilder.Sql("""
            WITH ranked AS (
                SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "EventId" ORDER BY "Name", "Id") - 1 AS position
                FROM "BTCPayServer.Plugins.SatfluxTickets"."TicketTypes"
            )
            UPDATE "BTCPayServer.Plugins.SatfluxTickets"."TicketTypes" AS t
            SET "DisplayOrder" = ranked.position::integer FROM ranked WHERE t."Id" = ranked."Id";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("DisplayOrder", "TicketTypes", "BTCPayServer.Plugins.SatfluxTickets");
        migrationBuilder.DropColumn("TicketTypeOrderRevision", "Events", "BTCPayServer.Plugins.SatfluxTickets");
    }
}
