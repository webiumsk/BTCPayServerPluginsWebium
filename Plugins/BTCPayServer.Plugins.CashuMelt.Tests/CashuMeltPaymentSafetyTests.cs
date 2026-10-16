using System.Text.Json;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.CashuMelt.Controllers;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

[Collection("Cashu database")]
public class CashuMeltPaymentSafetyTests
{
    [Fact]
    public void Classifier_preserves_exact_legacy_predicate_for_unknown_and_terminal_states()
    {
        var predicate = CashuMeltPaymentSafety.Unresolved.Compile();
        foreach (var state in new[] { null, "PENDING", "FAILED", "MANUAL_REVIEW", "MELT_COMPLETE", "SETTLED", "EXPIRED_UNPAID", "FUTURE" })
        for (var mask = 0; mask < 16; mask++)
        {
            var row = new CashuMeltPaymentRequest { SettlementState = state!,
                MintedProofsJson = (mask & 1) != 0 ? "" : null, MintIssuanceJson = (mask & 2) != 0 ? "" : null,
                BlankOutputsJson = (mask & 4) != 0 ? "" : null, MeltQuoteId = (mask & 8) != 0 ? "" : null };
            var expected = state is not ("SETTLED" or "EXPIRED_UNPAID") || (mask & 7) != 0 || ((mask & 8) != 0 && state != "SETTLED");
            Assert.Equal(expected, predicate(row));
            var diagnostic = CashuMeltPaymentSafety.Describe(new(state, true, (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0));
            Assert.Equal(expected, diagnostic.BlocksWalletChange);
            Assert.Equal(diagnostic.WalletChangeBlockReasons.Length, diagnostic.WalletChangeBlockReasons.Distinct().Count());
        }
        Assert.Equal("terminal_recovery_review", CashuMeltPaymentSafety.Describe(new("SETTLED", true, true, false, false, false)).RecoveryPhase);
        Assert.Equal("original_settings_required", CashuMeltPaymentSafety.Describe(new("FAILED", false, true, false, false, false)).RecoveryPhase);
        Assert.Equal("funding_verification", CashuMeltPaymentSafety.Describe(new("FAILED", true, false, false, false, false)).RecoveryPhase);
    }

    [CashuDatabaseFact]
    public async Task SQL_partition_and_existing_payment_DTO_explain_terminal_holds_without_loading_assets()
    {
        var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));
        await using var ctx = factory.CreateContext(); await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
        var store = Guid.NewGuid().ToString("N");
        CashuMeltPaymentRequest Row(string state) => new() { QuoteId = Guid.NewGuid().ToString("N"), StoreId = store, InvoiceId = "synthetic-invoice", SettlementState = state, SettingsSnapshotJson = "private-snapshot" };
        var failedA = Row("FAILED"); var failedB = Row("FAILED"); var terminal = Row("SETTLED"); terminal.MintedProofsJson = "secret-marker";
        var expired = Row("EXPIRED_UNPAID"); expired.BlankOutputsJson = "secret-marker";
        var future = Row("FUTURE"); var complete = Row("SETTLED"); complete.MeltQuoteId = "secret-marker";
        ctx.CashuMeltPaymentRequests.AddRange(failedA, failedB, terminal, expired, future, complete); await ctx.SaveChangesAsync();
        var service = new CashuMeltConfigService(factory);
        Assert.Equal(new CashuMeltConfigService.PaymentSafetyCounts(5, 2, 1, 2), await service.GetPaymentSafetyCountsAsync(store, default));
        Assert.Equal((5, 0), await service.GetUnresolvedFundsAsync(store, default));
        var api = new CashuMeltApiController(service, factory, null!, null!, null!, null!);
        var result = Assert.IsType<OkObjectResult>(await api.GetPayment(store, terminal.QuoteId));
        var dto = Assert.IsType<CashuMeltApiController.CashuMeltPaymentResponse>(result.Value);
        Assert.True(dto.BlocksWalletChange); Assert.Equal("terminal_recovery_review", dto.RecoveryPhase);
        Assert.Equal(new[] { "retained_minted_proofs" }, dto.WalletChangeBlockReasons);
        foreach (var json in new[] { JsonSerializer.Serialize(dto), Newtonsoft.Json.JsonConvert.SerializeObject(dto) })
        {
            Assert.DoesNotContain("secret-marker", json); Assert.DoesNotContain("private-snapshot", json);
            Assert.DoesNotContain("Diagnostic", json); Assert.DoesNotContain("MintedProofsJson", json);
        }
        var list = Assert.IsType<OkObjectResult>(await api.ListPayments(store));
        Assert.DoesNotContain("secret-marker", JsonSerializer.Serialize(list.Value));
        var safety = Assert.IsType<OkObjectResult>(await api.SettlementSafety(store, default));
        var payload = JsonSerializer.SerializeToElement(safety.Value);
        Assert.Equal(2, payload.GetProperty("failedPayments").GetInt32());
        Assert.Equal(2, payload.GetProperty("terminalRecoveryPayments").GetInt32());
        Assert.False(payload.GetProperty("safeToChangeWallet").GetBoolean());
    }
}
