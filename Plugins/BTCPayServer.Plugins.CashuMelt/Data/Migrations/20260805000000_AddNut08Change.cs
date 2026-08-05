using System;
using BTCPayServer.Plugins.CashuMelt.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.CashuMelt.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(CashuMeltDbContext))]
[Migration("20260805000000_AddNut08Change")]
public partial class AddNut08Change : Migration
{
    private const string Schema = "BTCPayServer.Plugins.CashuMelt";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BlankOutputsJson",
            schema: Schema,
            table: "CashuMeltPaymentRequests",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "CashuMeltChangeProofs",
            schema: Schema,
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                StoreId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                MintUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                Amount = table.Column<long>(type: "bigint", nullable: false),
                KeysetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Secret = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                C = table.Column<string>(type: "character varying(66)", maxLength: 66, nullable: false),
                State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                SourceQuoteId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                SweptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                SweepReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CashuMeltChangeProofs", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CashuMeltChangeProofs_Secret",
            schema: Schema,
            table: "CashuMeltChangeProofs",
            column: "Secret",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CashuMeltChangeProofs_StoreId_State",
            schema: Schema,
            table: "CashuMeltChangeProofs",
            columns: new[] { "StoreId", "State" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CashuMeltChangeProofs", schema: Schema);
        migrationBuilder.DropColumn(name: "BlankOutputsJson", schema: Schema, table: "CashuMeltPaymentRequests");
    }
}
