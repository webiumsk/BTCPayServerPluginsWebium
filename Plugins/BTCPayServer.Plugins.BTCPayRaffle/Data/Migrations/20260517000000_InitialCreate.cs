using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.BTCPayRaffle.Data.Migrations;

public partial class InitialCreate : Migration
{
    private const string Schema = "BTCPayServer.Plugins.BTCPayRaffle";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: Schema);

        migrationBuilder.CreateTable(
            name: "Raffles",
            schema: Schema,
            columns: t => new
            {
                Id            = t.Column<Guid>(nullable: false),
                Name          = t.Column<string>(maxLength: 200, nullable: false),
                Description   = t.Column<string>(maxLength: 2000, nullable: true),
                StoreId       = t.Column<string>(maxLength: 100, nullable: false),
                TicketPriceSats = t.Column<long>(nullable: false),
                MaxTickets    = t.Column<int>(nullable: true),
                Status        = t.Column<int>(nullable: false, defaultValue: 0),
                CreatedAt     = t.Column<DateTimeOffset>(nullable: false),
                OpenedAt      = t.Column<DateTimeOffset>(nullable: true),
                ClosedAt      = t.Column<DateTimeOffset>(nullable: true),
                CompletedAt   = t.Column<DateTimeOffset>(nullable: true)
            },
            constraints: t => t.PrimaryKey("PK_Raffles", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_Raffles_StoreId",
            schema: Schema,
            table: "Raffles",
            column: "StoreId");

        migrationBuilder.CreateTable(
            name: "RaffleTickets",
            schema: Schema,
            columns: t => new
            {
                Id           = t.Column<Guid>(nullable: false),
                RaffleId     = t.Column<Guid>(nullable: false),
                TicketNumber = t.Column<int>(nullable: false),
                InvoiceId    = t.Column<string>(maxLength: 50, nullable: false),
                BuyerEmail   = t.Column<string>(maxLength: 200, nullable: true),
                BuyerName    = t.Column<string>(maxLength: 200, nullable: true),
                AllocatedAt  = t.Column<DateTimeOffset>(nullable: false)
            },
            constraints: t =>
            {
                t.PrimaryKey("PK_RaffleTickets", x => x.Id);
                t.ForeignKey(
                    name: "FK_RaffleTickets_Raffles_RaffleId",
                    column: x => x.RaffleId,
                    principalSchema: Schema,
                    principalTable: "Raffles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RaffleTickets_InvoiceId",
            schema: Schema,
            table: "RaffleTickets",
            column: "InvoiceId");

        migrationBuilder.CreateIndex(
            name: "IX_RaffleTickets_RaffleId_TicketNumber",
            schema: Schema,
            table: "RaffleTickets",
            columns: new[] { "RaffleId", "TicketNumber" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "RaffleDrawings",
            schema: Schema,
            columns: t => new
            {
                Id              = t.Column<Guid>(nullable: false),
                RaffleId        = t.Column<Guid>(nullable: false),
                DrawOrder       = t.Column<int>(nullable: false),
                WinningTicketId = t.Column<Guid>(nullable: false),
                DrawnAt         = t.Column<DateTimeOffset>(nullable: false)
            },
            constraints: t =>
            {
                t.PrimaryKey("PK_RaffleDrawings", x => x.Id);
                t.ForeignKey(
                    name: "FK_RaffleDrawings_Raffles_RaffleId",
                    column: x => x.RaffleId,
                    principalSchema: Schema,
                    principalTable: "Raffles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                t.ForeignKey(
                    name: "FK_RaffleDrawings_RaffleTickets_WinningTicketId",
                    column: x => x.WinningTicketId,
                    principalSchema: Schema,
                    principalTable: "RaffleTickets",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateIndex(
            name: "IX_RaffleDrawings_RaffleId_DrawOrder",
            schema: Schema,
            table: "RaffleDrawings",
            columns: new[] { "RaffleId", "DrawOrder" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RaffleDrawings", schema: Schema);
        migrationBuilder.DropTable(name: "RaffleTickets",  schema: Schema);
        migrationBuilder.DropTable(name: "Raffles",        schema: Schema);
        migrationBuilder.DropSchema(name: Schema);
    }
}
