using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Controllers;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Plugins.TestFixtures;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

[Collection("Cashu database")]
public class CashuMeltPayoutRecoveryTests
{
    private sealed class Mint : HttpMessageHandler
    {
        public MeltQuoteResponse? Quote;
        public bool LoseResponse;
        public string ProofState = "UNSPENT";
        public int MeltCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object response;
            if (request.Method == HttpMethod.Get)
            {
                Assert.EndsWith("/v1/melt/quote/bolt11/original-payout", request.RequestUri!.AbsolutePath);
                if (LoseResponse) throw new HttpRequestException("Mint unavailable");
                response = Quote!;
            }
            else if (request.RequestUri!.AbsolutePath == "/v1/checkstate")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var states = body.RootElement.GetProperty("Ys").EnumerateArray().Select(y => new ProofStateInfo(y.GetString()!, ProofState, null)).ToArray();
                response = new CheckStateResponse(states);
            }
            else
            {
                Assert.Equal("/v1/melt/bolt11", request.RequestUri.AbsolutePath);
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("original-payout", body.RootElement.GetProperty("quote").GetString());
                MeltCalls++;
                throw new HttpRequestException("Lost response after sending the same payout intent");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }

    private async Task<(CashuMeltPaymentService Service, CashuMeltDbContext Context, CashuMeltPaymentRequest Request, CashuMeltProof[] Proofs, CashuMeltStoreSettings Settings)> Setup(Mint mint)
    {
        var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));
        var ctx = factory.CreateContext();
        await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
        var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
        var client = new CashuMeltMintClient(new HttpClient(mint), NullLogger<CashuMeltMintClient>.Instance);
        var service = new CashuMeltPaymentService(client, new CashuMeltConfigService(factory), factory, null!, null!, null!, null!, null!, NullLogger<CashuMeltPaymentService>.Instance, new CashuMeltMintIssuance(client, assets), assets);
        var (invoice, preimage) = LightningInvoiceFixture.Create(100);
        var request = new CashuMeltPaymentRequest { QuoteId = Guid.NewGuid().ToString("N"), StoreId = Guid.NewGuid().ToString("N"), InvoiceId = "invoice", CreatedAt = DateTimeOffset.UtcNow, AmountSats = 101, ForwardAmountSats = 100, ForwardBolt11 = invoice, MeltQuoteId = "original-payout", MintedProofsJson = assets.Protect("stored proofs"), MintIssuanceJson = assets.Protect("stored issuance") };
        mint.Quote ??= new MeltQuoteResponse("original-payout", 100, 1, "PAID", null, preimage, null, invoice);
        ctx.CashuMeltPaymentRequests.Add(request); await ctx.SaveChangesAsync();
        return (service, ctx, request, new[] { new CashuMeltProof(101, "keyset", "secret", "signature") }, new CashuMeltStoreSettings { MintUrl = "https://mint.example", LightningAddress = "unchanged@example.com" });
    }

    [CashuDatabaseFact]
    public async Task Payment_detail_never_exposes_a_mint_claim_url()
    {
        var setup = await Setup(new Mint()); await using var ctx = setup.Context;
        setup.Request.MintQuoteId = "protected:private-claim-id";
        setup.Request.MintUrl = "https://original-mint.example";
        await ctx.SaveChangesAsync();
        var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions
        { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));
        var controller = new CashuMeltApiController(new CashuMeltConfigService(factory), factory, setup.Service, null!, null!, null!);
        var result = Assert.IsType<OkObjectResult>(await controller.GetPayment(setup.Request.StoreId, setup.Request.QuoteId));
        var body = Assert.IsType<CashuMeltApiController.CashuMeltPaymentResponse>(result.Value);
        Assert.Equal(setup.Request.QuoteId, body.QuoteId);
        Assert.Null(body.MintQuotePollUrl);
        Assert.DoesNotContain("private-claim-id", JsonSerializer.Serialize(body));
    }

    [CashuDatabaseFact]
    public async Task Confirm_original_cannot_reset_finished_or_already_frozen_payments()
    {
        var setup = await Setup(new Mint()); await using var ctx = setup.Context;
        foreach (var state in new[] { "SETTLED", "MELT_COMPLETE", "PENDING", "FAILED", "EXPIRED_UNPAID" })
        {
            setup.Request.SettlementState = state;
            setup.Request.SettingsSnapshotJson = null;
            await ctx.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ConfirmOriginalSettingsAsync(
                setup.Request.StoreId, setup.Request.QuoteId, "https://mint.example", "a@example.com", default));
            await ctx.Entry(setup.Request).ReloadAsync();
            Assert.Equal(state, setup.Request.SettlementState);
            Assert.Null(setup.Request.SettingsSnapshotJson);
            Assert.NotNull(setup.Request.MintIssuanceJson);
        }
        setup.Request.SettlementState = "MANUAL_REVIEW";
        setup.Request.SettingsSnapshotJson = "original snapshot";
        await ctx.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ConfirmOriginalSettingsAsync(
            setup.Request.StoreId, setup.Request.QuoteId, "https://mint.example", "a@example.com", default));
        await ctx.Entry(setup.Request).ReloadAsync();
        Assert.Equal("MANUAL_REVIEW", setup.Request.SettlementState);
        Assert.Equal("original snapshot", setup.Request.SettingsSnapshotJson);
    }

    [CashuDatabaseFact]
    public async Task Manual_change_can_be_exported_only_after_expired_original_sweep_is_unpaid_and_unspent()
    {
        var mint = new Mint(); var setup = await Setup(mint); await using var ctx = setup.Context;
        var (expiredInvoice, _) = LightningInvoiceFixture.Create(100, TimeSpan.Zero);
        mint.Quote = mint.Quote! with { State = "UNPAID", PaymentPreimage = null, Request = expiredInvoice };
        var intent = JsonSerializer.Serialize(new { QuoteId = "original-payout", Bolt11 = expiredInvoice, Blinding = (object?)null });
        var row = new CashuMeltChangeProof
        {
            StoreId = setup.Request.StoreId, MintUrl = setup.Settings.MintUrl, Amount = 101, KeysetId = "keyset",
            C = Guid.NewGuid().ToString("N"), Secret = "merchant-bearer", State = "MANUAL_REVIEW",
            SweepReference = "original-payout", SweepIntentJson = intent, CreatedAt = DateTimeOffset.UtcNow
        };
        ctx.CashuMeltChangeProofs.Add(row); await ctx.SaveChangesAsync();
        mint.ProofState = "PENDING";
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ExportChangeAsync(row.StoreId, default));
        mint.ProofState = "UNSPENT"; mint.Quote = mint.Quote with { State = "PENDING" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ExportChangeAsync(row.StoreId, default));
        mint.Quote = mint.Quote with { State = "UNPAID", Quote = "wrong" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ExportChangeAsync(row.StoreId, default));
        mint.Quote = mint.Quote with { Quote = "original-payout", Request = setup.Request.ForwardBolt11 };
        row.SweepIntentJson = JsonSerializer.Serialize(new { QuoteId = "original-payout", Bolt11 = setup.Request.ForwardBolt11, Blinding = (object?)null });
        await ctx.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ExportChangeAsync(row.StoreId, default));
        mint.Quote = mint.Quote with { Request = expiredInvoice }; row.SweepIntentJson = intent; await ctx.SaveChangesAsync();
        Assert.Single(await setup.Service.ExportChangeAsync(row.StoreId, default));
        await ctx.Entry(row).ReloadAsync(); Assert.Equal("EXPORTED", row.State); Assert.NotNull(row.SweepIntentJson);
        mint.ProofState = "SPENT"; mint.Quote = mint.Quote with { State = "PAID" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ConfirmChangeRecoveredAsync(row.StoreId, default));
        mint.Quote = mint.Quote with { State = "UNPAID" };
        await setup.Service.ConfirmChangeRecoveredAsync(row.StoreId, default);
        await ctx.Entry(row).ReloadAsync(); Assert.Equal("RECOVERED", row.State); Assert.Null(row.SweepIntentJson);
    }

    [CashuDatabaseFact]
    public async Task Uncertain_manual_sweep_does_not_prevent_export_of_independently_available_change()
    {
        var setup = await Setup(new Mint { ProofState = "UNKNOWN" }); await using var ctx = setup.Context;
        var available = new CashuMeltChangeProof { StoreId = setup.Request.StoreId, MintUrl = setup.Settings.MintUrl,
            Amount = 1, KeysetId = "keyset", C = Guid.NewGuid().ToString("N"), Secret = "available-secret", State = "AVAILABLE", CreatedAt = DateTimeOffset.UtcNow };
        var uncertain = new CashuMeltChangeProof { StoreId = setup.Request.StoreId, MintUrl = setup.Settings.MintUrl,
            Amount = 1, KeysetId = "keyset", C = Guid.NewGuid().ToString("N"), Secret = "uncertain-secret", State = "MANUAL_REVIEW",
            SweepReference = "missing-original-intent", CreatedAt = DateTimeOffset.UtcNow };
        ctx.CashuMeltChangeProofs.AddRange(available, uncertain); await ctx.SaveChangesAsync();
        var token = Assert.Single(await setup.Service.ExportChangeAsync(available.StoreId, default));
        var encoded = token[6..].Replace('-', '+').Replace('_', '/');
        var payload = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
        var proof = Assert.Single(payload.RootElement.GetProperty("token")[0].GetProperty("proofs").EnumerateArray());
        Assert.Equal("available-secret", proof.GetProperty("secret").GetString());
        await ctx.Entry(available).ReloadAsync(); await ctx.Entry(uncertain).ReloadAsync();
        Assert.Equal("EXPORTED", available.State); Assert.Equal("MANUAL_REVIEW", uncertain.State);
        Assert.Equal("uncertain-secret", uncertain.Secret);
    }

    [CashuDatabaseFact]
    public async Task Private_change_export_is_repeatable_and_unknown_state_never_releases_the_guard()
    {
        var mint = new Mint(); var setup = await Setup(mint); await using var ctx = setup.Context;
        var row = new CashuMeltChangeProof { StoreId = setup.Request.StoreId, MintUrl = setup.Settings.MintUrl, Amount = 1, KeysetId = "keyset", C = Guid.NewGuid().ToString("N"), Secret = "merchant-bearer", State = "AVAILABLE", CreatedAt = DateTimeOffset.UtcNow };
        ctx.CashuMeltChangeProofs.Add(row); await ctx.SaveChangesAsync();
        var tokens = await setup.Service.ExportChangeAsync(row.StoreId, default);
        Assert.Single(tokens); Assert.StartsWith("cashuA", tokens[0]);
        Assert.Equal(tokens, await setup.Service.ExportChangeAsync(row.StoreId, default));
        mint.ProofState = "UNKNOWN";
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ConfirmChangeRecoveredAsync(row.StoreId, default));
        await ctx.Entry(row).ReloadAsync(); Assert.Equal("EXPORTED", row.State);
        mint.ProofState = "SPENT";
        await setup.Service.ConfirmChangeRecoveredAsync(row.StoreId, default);
        await ctx.Entry(row).ReloadAsync(); Assert.Equal("RECOVERED", row.State); Assert.Equal("consumed", row.Secret);
    }

    [CashuDatabaseFact]
    public async Task Paid_original_quote_completes_without_a_second_payout()
    {
        var mint = new Mint(); var setup = await Setup(mint); await using var ctx = setup.Context;
        var result = await setup.Service.MeltToMerchantAsync(setup.Settings, setup.Request, null!, setup.Proofs, ctx, default);
        Assert.True(result.Ok); Assert.Equal(0, mint.MeltCalls);
        Assert.Equal("MELT_COMPLETE", setup.Request.SettlementState);
        Assert.Null(setup.Request.MintedProofsJson); Assert.Null(setup.Request.MintIssuanceJson);
    }

    [CashuDatabaseFact]
    public async Task Spent_proofs_with_unavailable_original_quote_do_not_confirm_payment()
    {
        var mint = new Mint { LoseResponse = true, ProofState = "SPENT" }; var setup = await Setup(mint); await using var ctx = setup.Context;
        var result = await setup.Service.MeltToMerchantAsync(setup.Settings, setup.Request, null!, setup.Proofs, ctx, default);
        Assert.False(result.Ok); Assert.True(result.TransientMelt); Assert.Equal(0, mint.MeltCalls);
        Assert.NotNull(setup.Request.MintedProofsJson); Assert.Equal("original-payout", setup.Request.MeltQuoteId);
    }

    [CashuDatabaseFact]
    public async Task Wrong_paid_quote_is_quarantined_without_discarding_assets()
    {
        var mint = new Mint(); var setup = await Setup(mint); await using var ctx = setup.Context;
        mint.Quote = mint.Quote! with { Quote = "another-payout" };
        var result = await setup.Service.MeltToMerchantAsync(setup.Settings, setup.Request, null!, setup.Proofs, ctx, default);
        Assert.False(result.Ok); Assert.Equal("MANUAL_REVIEW", setup.Request.SettlementState);
        Assert.NotNull(setup.Request.MintedProofsJson); Assert.Equal(0, mint.MeltCalls);
    }

    [CashuDatabaseFact]
    public async Task Unpaid_unspent_retry_keeps_the_exact_original_payout_identity()
    {
        var mint = new Mint(); var setup = await Setup(mint); await using var ctx = setup.Context;
        mint.Quote = mint.Quote! with { State = "UNPAID", PaymentPreimage = null };
        var invoice = setup.Request.ForwardBolt11;
        var result = await setup.Service.MeltToMerchantAsync(setup.Settings, setup.Request, null!, setup.Proofs, ctx, default);
        Assert.False(result.Ok); Assert.True(result.TransientMelt); Assert.Equal(1, mint.MeltCalls);
        Assert.Equal("original-payout", setup.Request.MeltQuoteId); Assert.Equal(invoice, setup.Request.ForwardBolt11);
        Assert.NotNull(setup.Request.MintedProofsJson);
    }
}
