using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SatfluxTickets.Data.Migrations;

public partial class EventVenueTimeZone : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("TimeZone", "Events", "character varying(100)",
            schema: "BTCPayServer.Plugins.SatfluxTickets", maxLength: 100, nullable: true);
        // Product Owner decision: all existing physical events are in Slovakia.
        // Metadata only: scheduled UTC instants, reminders, tickets and orders remain untouched.
        migrationBuilder.Sql("""
            UPDATE "BTCPayServer.Plugins.SatfluxTickets"."Events"
            SET "TimeZone" = 'Europe/Bratislava'
            WHERE "TimeZone" IS NULL AND "EventType" = 1;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("TimeZone", "Events", "BTCPayServer.Plugins.SatfluxTickets");
}
