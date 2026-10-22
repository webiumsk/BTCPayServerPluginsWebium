using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

[Collection("Cashu database")]
public class CashuMeltSettingsToggleTests
{
    private static async Task<(CashuMeltDbContextFactory Factory, CashuMeltConfigService Config, string Store)> Setup()
    {
        var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));
        await using var db = factory.CreateContext(); await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(db);
        var store = Guid.NewGuid().ToString("N"); var config = new CashuMeltConfigService(factory);
        await config.SaveSettingsAsync(new CashuMeltStoreSettings { StoreId = store, MintUrl = "https://mint.example/Bitcoin", Unit = "sat",
            LightningAddress = "Merchant@example.com", TrustedMintUrls = "https://mint.example/Bitcoin", MaxMeltFeeReserveSats = 10, MaxMeltFeeReservePercentOfMinted = 20 });
        db.CashuMeltPaymentRequests.Add(new CashuMeltPaymentRequest { StoreId = store, InvoiceId = "synthetic-invoice", QuoteId = Guid.NewGuid().ToString("N"),
            SettlementState = "FAILED", SettlementError = "Mint returned HTTP 400", SettingsSnapshotJson = "original-snapshot",
            MintedProofsJson = "retained-encrypted-proof-marker", MintIssuanceJson = "retained-encrypted-issuance-marker" });
        await db.SaveChangesAsync();
        var settings = (await config.GetSettingsAsync(store))!; settings.Enabled = false; await config.SaveSettingsAsync(settings);
        return (factory, config, store);
    }

    [CashuDatabaseFact]
    public async Task Identical_risk_configuration_can_reenable_without_resolving_or_mutating_unknown_funding_or_proofs()
    {
        var h = await Setup(); var settings = (await h.Config.GetSettingsAsync(h.Store))!;
        settings.Enabled = true; await h.Config.SaveSettingsAsync(settings);
        Assert.True((await h.Config.GetSettingsAsync(h.Store))!.Enabled);
        Assert.Equal((1, 0), await h.Config.GetUnresolvedFundsAsync(h.Store, default));
        await using var db = h.Factory.CreateContext(); var row = await db.CashuMeltPaymentRequests.SingleAsync(r => r.StoreId == h.Store);
        Assert.Equal("FAILED", row.SettlementState); Assert.Equal("Mint returned HTTP 400", row.SettlementError);
        Assert.Equal("original-snapshot", row.SettingsSnapshotJson); Assert.Equal("retained-encrypted-proof-marker", row.MintedProofsJson);
        Assert.Equal("retained-encrypted-issuance-marker", row.MintIssuanceJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Config.DeleteSettingsAsync(h.Store));
        settings.LightningAddress = "replacement@example.com";
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Config.SaveSettingsAsync(settings));
    }

    [CashuDatabaseFact]
    public async Task Enable_with_any_changed_risk_field_retains_existing_outstanding_fund_guard()
    {
        var h = await Setup();
        var mutations = new Action<CashuMeltStoreSettings>[] {
            s => s.MintUrl = "https://mint.example/bitcoin", // Mint paths are case-sensitive.
            s => s.Unit = "usd",
            s => s.LightningAddress = "merchant@example.com", // Localpart identity must not be folded.
            s => s.TrustedMintUrls = "https://mint.example/Bitcoin\nhttps://other.example",
            s => s.MaxMeltFeeReserveSats = 11,
            s => s.MaxMeltFeeReservePercentOfMinted = 21,
        };
        foreach (var mutate in mutations)
        {
            var attempted = (await h.Config.GetSettingsAsync(h.Store))!; attempted.Enabled = true; mutate(attempted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Config.SaveSettingsAsync(attempted));
            Assert.False((await h.Config.GetSettingsAsync(h.Store))!.Enabled);
            Assert.Equal((1, 0), await h.Config.GetUnresolvedFundsAsync(h.Store, default));
        }
    }

    [CashuDatabaseFact]
    public async Task Existing_fee_and_trust_only_update_policy_is_preserved_and_then_same_risk_enable_is_allowed()
    {
        var h = await Setup(); var settings = (await h.Config.GetSettingsAsync(h.Store))!;
        settings.MaxMeltFeeReserveSats = 12; settings.MaxMeltFeeReservePercentOfMinted = 22;
        settings.TrustedMintUrls += "\nhttps://other.example";
        await h.Config.SaveSettingsAsync(settings);
        Assert.False((await h.Config.GetSettingsAsync(h.Store))!.Enabled);
        var reenabling = (await h.Config.GetSettingsAsync(h.Store))!; reenabling.Enabled = true;
        await h.Config.SaveSettingsAsync(reenabling);
        Assert.True((await h.Config.GetSettingsAsync(h.Store))!.Enabled);
        Assert.Equal((1, 0), await h.Config.GetUnresolvedFundsAsync(h.Store, default));
    }

    [CashuDatabaseFact]
    public async Task Same_risk_toggle_does_not_bypass_the_existing_exclusive_store_lease()
    {
        var h = await Setup(); var settings = (await h.Config.GetSettingsAsync(h.Store))!; settings.Enabled = true;
        await using (var busy = await CashuMeltDatabaseLease.TryAcquireAsync(h.Factory, "cashumelt:store:" + h.Store, false, default))
        {
            Assert.NotNull(busy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Config.SaveSettingsAsync(settings));
            Assert.False((await h.Config.GetSettingsAsync(h.Store))!.Enabled);
        }
        await h.Config.SaveSettingsAsync(settings);
        Assert.True((await h.Config.GetSettingsAsync(h.Store))!.Enabled);
    }
}
