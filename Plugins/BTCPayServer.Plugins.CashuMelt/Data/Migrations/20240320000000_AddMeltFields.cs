using BTCPayServer.Plugins.CashuMelt.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.CashuMelt.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(CashuMeltDbContext))]
    [Migration("20240320000000_AddMeltFields")]
    public partial class AddMeltFields : Migration
    {
        private const string Schema = "BTCPayServer.Plugins.CashuMelt";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MintedProofsJson",
                schema: Schema,
                table: "CashuMeltPaymentRequests",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeltQuoteId",
                schema: Schema,
                table: "CashuMeltPaymentRequests",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForwardBolt11",
                schema: Schema,
                table: "CashuMeltPaymentRequests",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MintedProofsJson", schema: Schema, table: "CashuMeltPaymentRequests");
            migrationBuilder.DropColumn(name: "MeltQuoteId",       schema: Schema, table: "CashuMeltPaymentRequests");
            migrationBuilder.DropColumn(name: "ForwardBolt11",     schema: Schema, table: "CashuMeltPaymentRequests");
        }
    }
}
