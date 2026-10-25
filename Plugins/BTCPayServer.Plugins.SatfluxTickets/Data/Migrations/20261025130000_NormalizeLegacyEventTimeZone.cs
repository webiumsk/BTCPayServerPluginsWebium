using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.SatfluxTickets.Data.Migrations;

public partial class NormalizeLegacyEventTimeZone : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Only normalize to an ID offered by this BTCPay host. On other platforms
        // leave the original evidence intact; unsupported venue metadata is reported
        // explicitly by the editor and public presentation instead of inventing UTC.
        if (!global::BTCPayServer.TimeZones.TryGet("Europe/Prague", out var zone)
            || !string.Equals(zone.Id, "Europe/Prague", StringComparison.Ordinal)) return;
        migrationBuilder.Sql("""
            UPDATE "BTCPayServer.Plugins.SatfluxTickets"."Events"
            SET "TimeZone" = 'Europe/Prague'
            WHERE "TimeZone" = 'Europe/Bratislava';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Prague is understood by 2.2.1 too. Canonical Prague rows cannot be
        // distinguished from normalized legacy rows, so do not rewrite either.
    }
}
