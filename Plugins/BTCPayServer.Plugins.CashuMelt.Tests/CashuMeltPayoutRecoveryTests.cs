using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.CashuMelt.Data;
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
