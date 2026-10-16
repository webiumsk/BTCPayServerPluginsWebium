using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Plugins.TestFixtures;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

[Collection("Cashu database")]
public class CashuMeltPhaseRecoveryTests
{
    private sealed class Mint : HttpMessageHandler
    {
        public HttpStatusCode FundingStatus = HttpStatusCode.BadRequest;
        public string ProofState = "UNSPENT";
        public MeltQuoteResponse Payout = null!;
        public int FundingCalls, RestoreCalls, MintCalls, MeltCalls, PayoutCalls, CheckCalls;
        public string RestoreMode = "complete";
        public bool PartialCheck;
        public HttpStatusCode PayoutStatus = HttpStatusCode.OK;
        public TaskCompletionSource? Entered, Release;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; object result;
            if (path.StartsWith("/v1/mint/quote/bolt11/"))
            {
                FundingCalls++;
                return new HttpResponseMessage(FundingStatus) { Content = new StringContent("private-mint-error") };
            }
            if (path == "/v1/mint/bolt11") { MintCalls++; throw new InvalidOperationException("Recovery must not issue new tokens"); }
            if (path == "/v1/restore")
            {
                RestoreCalls++; var input = JsonSerializer.Deserialize<MintTokensRequest>(await request.Content!.ReadAsStringAsync(ct))!;
                var outputs = RestoreMode == "empty" ? [] : RestoreMode == "partial" ? input.Outputs.Take(1).ToArray() : input.Outputs;
                result = new RestoreResponse(outputs, outputs.Select(o => new BlindSignature(o.Amount, o.Id, o.B_)).ToArray());
            }
            else if (path == "/v1/checkstate")
            {
                CheckCalls++;
                Entered?.TrySetResult(); if (Release != null) await Release.Task.WaitAsync(ct);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var ys = body.RootElement.GetProperty("Ys").EnumerateArray().Select(y => y.GetString()!).ToArray();
                result = new CheckStateResponse((PartialCheck ? ys.Take(1) : ys).Select(y => new ProofStateInfo(y, ProofState, null)).ToArray());
            }
            else if (path.StartsWith("/v1/melt/quote/bolt11/"))
            {
                PayoutCalls++;
                if (PayoutStatus != HttpStatusCode.OK) return new HttpResponseMessage(PayoutStatus);
                result = Payout;
            }
            else if (path == "/v1/melt/bolt11")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("original-payout", body.RootElement.GetProperty("quote").GetString());
                MeltCalls++; throw new HttpRequestException("Synthetic lost response after exact payout");
            }
            else throw new InvalidOperationException("Unexpected endpoint");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Database = "";
        public NpgsqlConnection Admin = null!;
        public CashuMeltDbContext Context = null!;
        public BTCPayServer.Data.ApplicationDbContext Core = null!;
        public CashuMeltPaymentService Service = null!;
        public CashuMeltAssetProtector Assets = null!;
        public CashuMeltPaymentRequest Row = null!;
        public CashuMeltStoreSettings Settings = null!;
        public Mint Mint = new();
        public CashuMeltProof[] Proofs = [];
        public CashuMeltMintIssuance.Intent Intent = null!;
        public InvoiceData Invoice = null!;
        public CashuMeltDbContextFactory Factory = null!;
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync(); await Core.DisposeAsync();
            await using var drop = new NpgsqlCommand("DROP DATABASE \"" + Database + "\" WITH (FORCE)", Admin);
            await drop.ExecuteNonQueryAsync(); await Admin.DisposeAsync();
        }
    }

    private static async Task<Harness> Setup()
    {
        var h = new Harness(); var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION"));
        Assert.Equal("cashu_safety", connection.Database);
        h.Admin = new NpgsqlConnection(connection.ConnectionString); await h.Admin.OpenAsync();
        h.Database = "cashu_safety_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand("CREATE DATABASE \"" + h.Database + "\"", h.Admin)) await create.ExecuteNonQueryAsync();
        connection.Database = h.Database; var options = Options.Create(new DatabaseOptions { ConnectionString = connection.ConnectionString });
        var coreFactory = new ApplicationDbContextFactory(options, NullLoggerFactory.Instance);
        h.Core = coreFactory.CreateContext(); await h.Core.Database.EnsureCreatedAsync();
        h.Factory = new CashuMeltDbContextFactory(options); h.Context = h.Factory.CreateContext();
        await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(h.Context);
        h.Settings = new() { StoreId = "store", MintUrl = "https://mint.example", LightningAddress = "original@example.com", Unit = "sat", Enabled = true };
        h.Core.Stores.Add(new StoreData { Id = "store", StoreName = "Synthetic recovery" });
        var entity = new InvoiceEntity { Id = "invoice", StoreId = "store", Currency = "BTC", MonitoringExpiration = DateTimeOffset.UtcNow.AddHours(-1) };
        h.Invoice = new InvoiceData { Id = entity.Id, StoreDataId = entity.StoreId, Currency = "BTC", Status = "Expired" };
        h.Invoice.SetBlob(entity); h.Core.Invoices.Add(h.Invoice); await h.Core.SaveChangesAsync();
        h.Assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
        var key = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
        var keyset = new MintKeyset("009a1f293253e41e", "sat", new Dictionary<string, string> { ["1"] = key, ["2"] = key, ["4"] = key });
        var outputs = new long[] { 1, 2, 4 }.Select(amount =>
        {
            var secret = Guid.NewGuid().ToString("N"); var (blinded, r) = CashuMeltCrypto.CreateBlindedMessage(Encoding.UTF8.GetBytes(secret));
            return new CashuMeltMintIssuance.Output(amount, secret, Convert.ToHexString(r), new BlindedMessage(amount, keyset.Id, blinded));
        }).ToArray();
        h.Intent = new(h.Settings.MintUrl, "private-funding", "sat", 7, keyset, outputs);
        h.Proofs = outputs.Select(o => new CashuMeltProof(o.Amount, keyset.Id, o.Secret, CashuMeltCrypto.ComputeYHex(Encoding.UTF8.GetBytes(o.Secret)))).ToArray();
        var (funding, _) = LightningInvoiceFixture.Create(7, TimeSpan.Zero);
        var (payout, preimage) = LightningInvoiceFixture.Create(5);
        h.Row = new() { QuoteId = "local-quote", StoreId = "store", InvoiceId = "invoice", AmountSats = 7, Unit = "sat", Bolt11Invoice = funding,
            MintUrl = h.Settings.MintUrl, MintQuoteId = h.Assets.Protect("private-funding"), SettingsSnapshotJson = JsonSerializer.Serialize(h.Settings),
            MintIssuanceJson = h.Assets.Protect(JsonSerializer.Serialize(h.Intent)), MintedProofsJson = h.Assets.Protect(JsonSerializer.Serialize(h.Proofs, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })),
            MeltQuoteId = "original-payout", ForwardAmountSats = 5, ForwardBolt11 = payout, SettlementState = "FAILED", SettlementError = "Mint returned HTTP 400", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        h.Mint.Payout = new MeltQuoteResponse("original-payout", 5, 2, "PENDING", null, preimage, null, payout);
        h.Context.CashuMeltPaymentRequests.Add(h.Row); await h.Context.SaveChangesAsync();
        var config = new CashuMeltConfigService(h.Factory);
        var client = new CashuMeltMintClient(new HttpClient(h.Mint), NullLogger<CashuMeltMintClient>.Instance);
        h.Service = new(client, config, h.Factory, new InvoiceRepository(coreFactory, null!), null!, null!, new PaymentMethodHandlerDictionary([]), null!,
            NullLogger<CashuMeltPaymentService>.Instance, new CashuMeltMintIssuance(client, h.Assets), h.Assets,
            new CashuMeltQuoteService(client, config, h.Factory, h.Assets, NullLogger<CashuMeltQuoteService>.Instance));
        return h;
    }

    [CashuDatabaseFact]
    public async Task Durable_proofs_reconcile_exact_payout_without_obsolete_funding_lookup_or_second_melt()
    {
        await using var h = await Setup();
        var outcome = await h.Service.RetrySettlementAsync("store", "local-quote");
        Assert.False(outcome.Settled); Assert.Equal(3, outcome.RetryAfterSeconds);
        Assert.Equal(0, h.Mint.FundingCalls); Assert.Equal(1, h.Mint.PayoutCalls); Assert.Equal(0, h.Mint.MeltCalls);
        await h.Context.Entry(h.Row).ReloadAsync(); Assert.NotNull(h.Row.MintedProofsJson); Assert.Equal("original-payout", h.Row.MeltQuoteId);
    }

    [CashuDatabaseFact]
    public async Task Empty_or_partial_restore_never_issues_new_tokens_and_preserves_intent()
    {
        await using var h = await Setup(); h.Row.MintedProofsJson = null; await h.Context.SaveChangesAsync();
        foreach (var mode in new[] { "empty", "partial" })
        {
            h.Mint.RestoreMode = mode;
            await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
            Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.NotNull(h.Row.MintIssuanceJson);
            Assert.Null(h.Row.MintedProofsJson); Assert.Equal(0, h.Mint.FundingCalls); Assert.Equal(0, h.Mint.MintCalls); Assert.Equal(0, h.Mint.MeltCalls);
        }
    }

    [CashuDatabaseFact]
    public async Task Complete_restore_recovers_original_outputs_without_new_issuance()
    {
        await using var h = await Setup(); h.Row.MintedProofsJson = null; await h.Context.SaveChangesAsync();
        await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.NotNull(h.Row.MintedProofsJson); Assert.Equal(1, h.Mint.RestoreCalls); Assert.Equal(0, h.Mint.MintCalls);
        Assert.Equal(0, h.Mint.FundingCalls); Assert.Equal(1, h.Mint.PayoutCalls);
    }

    [CashuDatabaseFact]
    public async Task Proof_only_recovery_requires_complete_unspent_evidence_before_initial_payout()
    {
        await using var h = await Setup(); h.Row.MeltQuoteId = null; h.Row.ForwardAmountSats = null; h.Row.ForwardBolt11 = null; await h.Context.SaveChangesAsync();
        foreach (var state in new[] { "UNKNOWN", "SPENT", "PENDING" })
        {
            h.Mint.ProofState = state;
            var outcome = await h.Service.RetrySettlementAsync("store", "local-quote");
            Assert.False(outcome.Settled); Assert.Equal(0, h.Mint.MeltCalls); Assert.Equal(0, h.Mint.FundingCalls);
            await h.Context.Entry(h.Row).ReloadAsync(); Assert.NotNull(h.Row.MintedProofsJson);
        }
        Assert.Equal(3, h.Mint.CheckCalls);
    }

    [CashuDatabaseFact]
    public async Task Missing_original_data_corrupt_material_and_invoice_review_never_reach_mint()
    {
        await using var h = await Setup();
        h.Row.MintedProofsJson = h.Assets.Protect("not-json"); await h.Context.SaveChangesAsync();
        await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.Equal(0, h.Mint.PayoutCalls);
        h.Row.SettingsSnapshotJson = null; h.Row.SettlementState = "FAILED"; await h.Context.SaveChangesAsync();
        await h.Service.RetrySettlementAsync("store", "local-quote");
        Assert.Equal(0, h.Mint.FundingCalls); Assert.Equal(0, h.Mint.RestoreCalls);
    }

    [CashuDatabaseFact]
    public async Task Funding_errors_without_durable_material_keep_the_payment_obligation()
    {
        await using var h = await Setup(); h.Row.MintedProofsJson = null; h.Row.MintIssuanceJson = null; h.Row.MeltQuoteId = null;
        await h.Context.SaveChangesAsync();
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound })
        {
            h.Mint.FundingStatus = status;
            var outcome = await h.Service.RetrySettlementAsync("store", "local-quote");
            Assert.False(outcome.Settled); Assert.Contains(((int)status).ToString(), outcome.Error);
            Assert.Equal((1, 0), await new CashuMeltConfigService(h.Factory).GetUnresolvedFundsAsync("store", default));
        }
        Assert.Equal(2, h.Mint.FundingCalls); Assert.Equal(0, h.Mint.MeltCalls);
    }

    [CashuDatabaseFact]
    public async Task Automatic_counter_is_monotonic_stops_at_twenty_and_manual_retry_resets_it()
    {
        await using var h = await Setup();
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            await h.Service.RetrySettlementAutomaticallyAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
            Assert.Equal(attempt, h.Row.RetryCount);
        }
        Assert.True(h.Row.NeedsManualReview); Assert.Equal(19, h.Mint.PayoutCalls);
        await h.Service.RetrySettlementAutomaticallyAsync("store", "local-quote"); Assert.Equal(19, h.Mint.PayoutCalls);
        await h.Service.CheckAndRecordPaymentAsync("local-quote"); Assert.Equal(19, h.Mint.PayoutCalls);
        await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.Equal(0, h.Row.RetryCount); Assert.False(h.Row.NeedsManualReview); Assert.Equal(20, h.Mint.PayoutCalls);
    }

    [CashuDatabaseFact]
    public async Task Concurrent_automatic_retry_cannot_increment_or_dispatch_while_quote_is_leased()
    {
        await using var h = await Setup(); h.Row.MeltQuoteId = null; await h.Context.SaveChangesAsync();
        h.Mint.ProofState = "UNKNOWN"; h.Mint.Entered = new(); h.Mint.Release = new();
        var first = h.Service.RetrySettlementAutomaticallyAsync("store", "local-quote");
        await h.Mint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var concurrent = await h.Service.RetrySettlementAutomaticallyAsync("store", "local-quote");
        Assert.Equal(3, concurrent.RetryAfterSeconds); h.Mint.Release.SetResult(); await first;
        await h.Context.Entry(h.Row).ReloadAsync(); Assert.Equal(1, h.Row.RetryCount); Assert.Equal(1, h.Mint.CheckCalls);
    }

    [CashuDatabaseFact]
    public async Task Missing_or_mismatched_issuance_binding_cannot_authorize_proof_recovery()
    {
        await using var h = await Setup();
        foreach (var invalid in new string?[] { null, h.Assets.Protect("invalid-json"),
            h.Assets.Protect(JsonSerializer.Serialize(h.Intent with { QuoteId = "other-funding" })),
            h.Assets.Protect(JsonSerializer.Serialize(h.Intent with { Amount = 8 })) })
        {
            h.Row.MintIssuanceJson = invalid; h.Row.SettlementState = "FAILED"; h.Row.NeedsManualReview = false;
            await h.Context.SaveChangesAsync(); await h.Service.RetrySettlementAsync("store", "local-quote");
            await h.Context.Entry(h.Row).ReloadAsync();
            Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.NotNull(h.Row.MintedProofsJson);
            Assert.Equal(0, h.Mint.FundingCalls); Assert.Equal(0, h.Mint.MeltCalls); Assert.Equal(0, h.Mint.PayoutCalls);
        }
        h.Row.MintIssuanceJson = h.Assets.Protect(JsonSerializer.Serialize(h.Intent));
        h.Row.MintedProofsJson = h.Assets.Protect(JsonSerializer.Serialize(h.Proofs.Select(p => p with { Secret = "wrong-proof" }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        await h.Context.SaveChangesAsync(); await h.Service.RetrySettlementAsync("store", "local-quote");
        await h.Context.Entry(h.Row).ReloadAsync(); Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.Equal(0, h.Mint.PayoutCalls);
    }

    [CashuDatabaseFact]
    public async Task Settled_or_invalid_original_invoice_requires_review_before_any_recovery_network_call()
    {
        await using var h = await Setup();
        foreach (var status in new[] { "Settled", "Invalid" })
        {
            h.Invoice.Status = status; await h.Core.SaveChangesAsync();
            await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
            Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.NotNull(h.Row.MintedProofsJson);
            Assert.Equal(0, h.Mint.PayoutCalls); Assert.Equal(0, h.Mint.FundingCalls); Assert.Equal(0, h.Mint.MeltCalls);
        }
    }

    [CashuDatabaseFact]
    public async Task Exact_paid_payout_finalizes_only_accounting_without_funding_poll_or_new_melt()
    {
        await using var h = await Setup(); h.Mint.Payout = h.Mint.Payout with { State = "PAID" };
        var outcome = await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.False(outcome.Settled); // Test deliberately has no accounting handler; payout is retained as complete.
        Assert.Equal("MELT_COMPLETE", h.Row.SettlementState); Assert.Null(h.Row.MintedProofsJson); Assert.Null(h.Row.MintIssuanceJson);
        Assert.Equal("original-payout", h.Row.MeltQuoteId); Assert.Equal(0, h.Mint.MeltCalls); Assert.Equal(0, h.Mint.FundingCalls);
        await h.Service.RetrySettlementAutomaticallyAsync("store", "local-quote");
        Assert.Equal(1, h.Mint.PayoutCalls); Assert.Equal(0, h.Mint.MeltCalls);
    }

    [CashuDatabaseFact]
    public async Task Wrong_paid_receipt_or_unknown_payout_cannot_clear_or_replace_original_intent()
    {
        await using var h = await Setup();
        h.Mint.PayoutStatus = HttpStatusCode.NotFound;
        await h.Service.RetrySettlementAsync("store", "local-quote");
        await h.Context.Entry(h.Row).ReloadAsync(); Assert.NotNull(h.Row.MintedProofsJson);
        h.Mint.PayoutStatus = HttpStatusCode.OK;
        h.Mint.Payout = h.Mint.Payout with { State = "PAID", Quote = "other-payout" };
        await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.NotNull(h.Row.MintedProofsJson);
        Assert.Equal("original-payout", h.Row.MeltQuoteId); Assert.Equal(0, h.Mint.MeltCalls);
        h.Mint.Payout = h.Mint.Payout with { Quote = "original-payout", PaymentPreimage = new string('0', 64) };
        await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.NotNull(h.Row.MintedProofsJson); Assert.Equal(0, h.Mint.MeltCalls);
    }

    [CashuDatabaseFact]
    public async Task Unpaid_original_payout_retries_exact_intent_only_and_preserves_lost_response()
    {
        await using var h = await Setup(); h.Mint.Payout = h.Mint.Payout with { State = "UNPAID" };
        await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.Equal(1, h.Mint.MeltCalls); Assert.Equal(1, h.Mint.CheckCalls); Assert.Equal(0, h.Mint.FundingCalls);
        Assert.Equal("original-payout", h.Row.MeltQuoteId); Assert.NotNull(h.Row.MintedProofsJson);
        var (expired, _) = LightningInvoiceFixture.Create(5, TimeSpan.Zero);
        h.Row.ForwardBolt11 = expired; h.Mint.Payout = h.Mint.Payout with { Request = expired }; await h.Context.SaveChangesAsync();
        await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.Equal(1, h.Mint.MeltCalls); Assert.NotNull(h.Row.MintedProofsJson);
    }

    [CashuDatabaseFact]
    public async Task Partial_checkstate_or_material_without_proofs_preserves_obligation()
    {
        await using var h = await Setup(); h.Row.MeltQuoteId = null; h.Mint.PartialCheck = true; await h.Context.SaveChangesAsync();
        await h.Service.RetrySettlementAsync("store", "local-quote"); Assert.Equal(0, h.Mint.MeltCalls);
        await h.Context.Entry(h.Row).ReloadAsync(); h.Row.MintedProofsJson = null; h.Row.MintIssuanceJson = null;
        h.Row.BlankOutputsJson = h.Assets.Protect("[]"); await h.Context.SaveChangesAsync();
        await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
        Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.NotNull(h.Row.BlankOutputsJson);
        Assert.Equal(0, h.Mint.FundingCalls); Assert.Equal(0, h.Mint.MintCalls);
    }
    [CashuDatabaseFact]
    public async Task Equal_denomination_duplicate_member_missing_and_extra_proofs_cannot_bypass_bijection()
    {
        await using var h = await Setup();
        var outputs = new long[] { 1, 2, 2, 2 }.Select(amount =>
        {
            var secret = Guid.NewGuid().ToString("N");
            var (blinded, r) = CashuMeltCrypto.CreateBlindedMessage(Encoding.UTF8.GetBytes(secret));
            return new CashuMeltMintIssuance.Output(amount, secret, Convert.ToHexString(r), new BlindedMessage(amount, h.Intent.Keyset.Id, blinded));
        }).ToArray();
        var intent = h.Intent with { Outputs = outputs };
        h.Row.MintIssuanceJson = h.Assets.Protect(JsonSerializer.Serialize(intent));
        var complete = outputs.Select(o => new CashuMeltProof(o.Amount, intent.Keyset.Id, o.Secret, CashuMeltCrypto.ComputeYHex(Encoding.UTF8.GetBytes(o.Secret)))).ToArray();
        var duplicateMember = complete.ToArray(); duplicateMember[1] = complete[2];
        // Every member is valid and total is still seven; only uniqueness detects the substitution.
        Assert.Equal(7, duplicateMember.Sum(p => p.Amount));
        foreach (var invalid in new[] { duplicateMember, complete[..^1], complete.Append(complete[1]).ToArray() })
        {
            var encrypted = h.Assets.Protect(JsonSerializer.Serialize(invalid));
            h.Row.MintedProofsJson = encrypted; h.Row.SettlementState = "FAILED"; h.Row.NeedsManualReview = false;
            await h.Context.SaveChangesAsync();
            await h.Service.RetrySettlementAsync("store", "local-quote"); await h.Context.Entry(h.Row).ReloadAsync();
            Assert.Equal("MANUAL_REVIEW", h.Row.SettlementState); Assert.Equal(encrypted, h.Row.MintedProofsJson);
            Assert.NotNull(h.Row.MintIssuanceJson); Assert.Equal("original-payout", h.Row.MeltQuoteId);
            Assert.Equal(0, h.Mint.PayoutCalls); Assert.Equal(0, h.Mint.MeltCalls);
            Assert.Equal(0, h.Mint.RestoreCalls); Assert.Equal(0, h.Mint.MintCalls); Assert.Equal(0, h.Mint.FundingCalls);
        }
    }

}
