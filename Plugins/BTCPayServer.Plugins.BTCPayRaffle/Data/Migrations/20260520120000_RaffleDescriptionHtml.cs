using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.BTCPayRaffle.Data.Migrations;

[DbContext(typeof(RaffleDbContext))]
[Migration("20260520120000_RaffleDescriptionHtml")]
public partial class RaffleDescriptionHtml : Migration
{
    private const string Schema = "BTCPayServer.Plugins.BTCPayRaffle";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Description",
            schema: Schema,
            table: "Raffles",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(2000)",
            oldMaxLength: 2000,
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Description",
            schema: Schema,
            table: "Raffles",
            type: "character varying(2000)",
            maxLength: 2000,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(8000)",
            oldMaxLength: 8000,
            oldNullable: true);
    }
}
