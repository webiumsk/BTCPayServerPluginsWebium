using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.BTCPayRaffle.Data.Migrations;

[DbContext(typeof(RaffleDbContext))]
[Migration("20260519000000_BuyerEmailIndex")]
public partial class BuyerEmailIndex : Migration
{
    private const string Schema = "BTCPayServer.Plugins.BTCPayRaffle";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_RaffleTickets_RaffleId_BuyerEmail",
            schema: Schema,
            table: "RaffleTickets",
            columns: new[] { "RaffleId", "BuyerEmail" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_RaffleTickets_RaffleId_BuyerEmail",
            schema: Schema,
            table: "RaffleTickets");
    }
}
