using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.BTCPayRaffle.Data.Migrations;

[DbContext(typeof(RaffleDbContext))]
[Migration("20260518000000_RafflePricingAndManualTickets")]
public partial class RafflePricingAndManualTickets : Migration
{
    private const string Schema = "BTCPayServer.Plugins.BTCPayRaffle";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TicketCurrency",
            schema: Schema,
            table: "Raffles",
            type: "character varying(10)",
            maxLength: 10,
            nullable: false,
            defaultValue: "SATS");

        migrationBuilder.AddColumn<decimal>(
            name: "TicketPrice",
            schema: Schema,
            table: "Raffles",
            type: "numeric(18,8)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.Sql($"""
            UPDATE "{Schema}"."Raffles"
            SET "TicketCurrency" = 'SATS', "TicketPrice" = "TicketPriceSats"
            WHERE "TicketPrice" = 0;
            """);

        migrationBuilder.AddColumn<bool>(
            name: "IsManual",
            schema: Schema,
            table: "RaffleTickets",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AlterColumn<string>(
            name: "InvoiceId",
            schema: Schema,
            table: "RaffleTickets",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "TicketCurrency", schema: Schema, table: "Raffles");
        migrationBuilder.DropColumn(name: "TicketPrice", schema: Schema, table: "Raffles");
        migrationBuilder.DropColumn(name: "IsManual", schema: Schema, table: "RaffleTickets");

        migrationBuilder.AlterColumn<string>(
            name: "InvoiceId",
            schema: Schema,
            table: "RaffleTickets",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(100)",
            oldMaxLength: 100);
    }
}
