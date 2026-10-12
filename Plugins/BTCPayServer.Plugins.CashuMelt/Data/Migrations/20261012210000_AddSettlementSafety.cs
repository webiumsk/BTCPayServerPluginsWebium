using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.CashuMelt.Data.Migrations;

[DbContext(typeof(CashuMeltDbContext))]
[Migration("20261012210000_AddSettlementSafety")]
public class AddSettlementSafety : Migration
{
    private const string Schema = "BTCPayServer.Plugins.CashuMelt";
    protected override void Up(MigrationBuilder m)
    {
        m.AddColumn<string>(name: "MintQuoteId", schema: Schema, table: "CashuMeltPaymentRequests", type: "text", nullable: true);
        m.AddColumn<string>(name: "MintUrl", schema: Schema, table: "CashuMeltPaymentRequests", type: "text", nullable: true);
        m.AddColumn<string>(name: "SettingsSnapshotJson", schema: Schema, table: "CashuMeltPaymentRequests", type: "text", nullable: true);
        m.AddColumn<string>(name: "MintIssuanceJson", schema: Schema, table: "CashuMeltPaymentRequests", type: "text", nullable: true);
        m.AddColumn<DateTimeOffset>(name: "QuoteExpiresAt", schema: Schema, table: "CashuMeltPaymentRequests", type: "timestamp with time zone", nullable: true);
        m.AddColumn<DateTimeOffset>(name: "LastCheckedAt", schema: Schema, table: "CashuMeltPaymentRequests", type: "timestamp with time zone", nullable: true);
        m.AddColumn<long>(name: "ForwardAmountSats", schema: Schema, table: "CashuMeltPaymentRequests", type: "bigint", nullable: true);
        m.AddColumn<long>(name: "MeltFeeReserveSats", schema: Schema, table: "CashuMeltPaymentRequests", type: "bigint", nullable: true);
        m.AddColumn<long>(name: "InputFeeSats", schema: Schema, table: "CashuMeltPaymentRequests", type: "bigint", nullable: true);
        m.AddColumn<string>(name: "SweepIntentJson", schema: Schema, table: "CashuMeltChangeProofs", type: "text", nullable: true);
        m.Sql($"ALTER TABLE \"{Schema}\".\"CashuMeltChangeProofs\" ALTER COLUMN \"Secret\" TYPE text");
        m.DropIndex(name: "IX_CashuMeltChangeProofs_Secret", schema: Schema, table: "CashuMeltChangeProofs");
        m.CreateIndex(name: "IX_CashuMeltChangeProofs_MintUrl_KeysetId_C", schema: Schema, table: "CashuMeltChangeProofs", columns: new[] { "MintUrl", "KeysetId", "C" }, unique: true);
    }
    protected override void Down(MigrationBuilder m)
    {
        // Asset records must be recovered/exported before downgrading. Never silently discard recovery material.
        throw new NotSupportedException("Settlement safety migration requires an explicit recovery-aware downgrade");
    }
}
