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
using Xunit;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

[Collection("Cashu database")]
public class CashuMeltFundingRecoveryTests
{
    private sealed class FundingMint : HttpMessageHandler
    {
        public MintQuoteBolt11Response Quote = null!;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method); // Recovery must never issue tokens or pay Lightning.
            Assert.EndsWith("/v1/mint/quote/bolt11/" + Quote.Quote, request.RequestUri!.AbsolutePath);
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(JsonSerializer.Serialize(Quote,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }), Encoding.UTF8, "application/json")
            });
        }
    }

    [CashuDatabaseFact]
    public async Task Real_invoice_monitoring_and_exact_mint_status_release_abandoned_quotes_without_adopting_current_wallet()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION"));
        Assert.Equal("cashu_safety", connection.Database);
        var database = "cashu_safety_" + Guid.NewGuid().ToString("N");
        await using var admin = new Npgsql.NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new Npgsql.NpgsqlCommand("CREATE DATABASE \"" + database + "\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            var options = Options.Create(new DatabaseOptions { ConnectionString = connection.ConnectionString });
            var coreFactory = new ApplicationDbContextFactory(options, NullLoggerFactory.Instance);
            await using var core = coreFactory.CreateContext();
            await core.Database.EnsureCreatedAsync();
            var factory = new CashuMeltDbContextFactory(options);
            await using var ctx = factory.CreateContext();
            await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
            var config = new CashuMeltConfigService(factory);
            var settings = new CashuMeltStoreSettings { StoreId = "store", MintUrl = "https://mint.example", LightningAddress = "new@example.com", Enabled = true };
            await config.SaveSettingsAsync(settings);
            core.Stores.Add(new StoreData { Id = "store", StoreName = "Recovery test" });
            var invoice = new InvoiceEntity { Id = "invoice", StoreId = "store", Currency = "BTC", MonitoringExpiration = DateTimeOffset.UtcNow.AddHours(-1) };
            var data = new InvoiceData { Id = invoice.Id, StoreDataId = invoice.StoreId, Currency = "BTC", Status = "Expired" };
            data.SetBlob(invoice); core.Invoices.Add(data); await core.SaveChangesAsync();
            var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
            var mint = new FundingMint();
            var client = new CashuMeltMintClient(new HttpClient(mint), NullLogger<CashuMeltMintClient>.Instance);
            var service = new CashuMeltPaymentService(client, config, factory, new InvoiceRepository(coreFactory, null!),
                null!, null!, null!, null!, NullLogger<CashuMeltPaymentService>.Instance, new CashuMeltMintIssuance(client, assets), assets, new CashuMeltQuoteService(client, new CashuMeltConfigService(factory), factory, assets, NullLogger<CashuMeltQuoteService>.Instance));
            settings.Enabled = false;
            await config.SaveSettingsAsync(settings);
            Assert.Equal((null, null, "CashuMelt not configured for this store"), await service.CreateQuoteAsync(invoice.Id, "store", 100, "sat"));
            Assert.Equal(0, mint.Calls);
            var (bolt11, _) = LightningInvoiceFixture.Create(100, TimeSpan.Zero);
            foreach (var state in new[] { "PENDING", "FAILED", "MANUAL_REVIEW" })
            {
                var id = Guid.NewGuid().ToString("N");
                var row = new CashuMeltPaymentRequest { QuoteId = id, StoreId = "store", InvoiceId = invoice.Id, AmountSats = 100,
                    Bolt11Invoice = bolt11, SettlementState = state, CreatedAt = DateTimeOffset.UtcNow };
                ctx.CashuMeltPaymentRequests.Add(row); await ctx.SaveChangesAsync();
                mint.Quote = new MintQuoteBolt11Response(id, bolt11, 100, "sat", "UNPAID", null, 0, 0);
                Assert.Equal((false, null, null), await service.CheckAndRecordPaymentAsync(id));
                await ctx.Entry(row).ReloadAsync();
                Assert.Equal("EXPIRED_UNPAID", row.SettlementState);
                Assert.Null(row.SettingsSnapshotJson); Assert.Null(row.MintUrl); // Current payout was never adopted.
                Assert.Equal((0, 0), await config.GetUnresolvedFundsAsync("store", default));
                await service.RetrySettlementAsync("store", id); await ctx.Entry(row).ReloadAsync();
                Assert.Equal("EXPIRED_UNPAID", row.SettlementState);
            }

            var failed = new CashuMeltPaymentRequest { QuoteId = "failed", StoreId = "store", InvoiceId = invoice.Id,
                AmountSats = 100, Bolt11Invoice = bolt11, SettlementState = "FAILED", CreatedAt = DateTimeOffset.UtcNow };
            ctx.CashuMeltPaymentRequests.Add(failed); await ctx.SaveChangesAsync();
            mint.Quote = new MintQuoteBolt11Response("failed", bolt11, 100, "sat", "UNPAID", null, 0, 0);
            mint.Status = HttpStatusCode.NotFound;
            await service.CheckAndRecordPaymentAsync("failed");
            Assert.Equal((1, 0), await config.GetUnresolvedFundsAsync("store", default));
            settings.LightningAddress = "replacement@example.com";
            await Assert.ThrowsAsync<InvalidOperationException>(() => config.SaveSettingsAsync(settings));
            mint.Status = HttpStatusCode.OK;
            invoice.MonitoringExpiration = DateTimeOffset.UtcNow.AddHours(1); data.SetBlob(invoice); await core.SaveChangesAsync();
            await service.RetrySettlementAsync("store", "failed"); await ctx.Entry(failed).ReloadAsync();
            Assert.Equal("FAILED", failed.SettlementState);
            invoice.MonitoringExpiration = DateTimeOffset.UtcNow.AddHours(-1); data.SetBlob(invoice); await core.SaveChangesAsync();
            mint.Quote = mint.Quote with { State = "PAID", AmountPaid = 100 };
            await service.CheckAndRecordPaymentAsync("failed"); await ctx.Entry(failed).ReloadAsync();
            Assert.Equal("MANUAL_REVIEW", failed.SettlementState);
            Assert.Null(failed.SettingsSnapshotJson); Assert.Equal((1, 0), await config.GetUnresolvedFundsAsync("store", default));

            // A permanent funding-status 400 is unknown payment state, not proof of unpaid.
            // Stop automatic/checkout polling while preserving the merchant's explicit retry.
            var invalidQuoteId = "invalid-status-quote";
            var invalidQuote = new CashuMeltPaymentRequest
            {
                QuoteId = invalidQuoteId,
                StoreId = "store",
                InvoiceId = invoice.Id,
                AmountSats = 100,
                Unit = "sat",
                Bolt11Invoice = bolt11,
                MintUrl = settings.MintUrl,
                MintQuoteId = assets.Protect(invalidQuoteId),
                SettingsSnapshotJson = JsonSerializer.Serialize(settings),
                SettlementState = "PENDING",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
            };
            ctx.CashuMeltPaymentRequests.Add(invalidQuote);
            await ctx.SaveChangesAsync();
            mint.Quote = new MintQuoteBolt11Response(invalidQuoteId, bolt11, 100, "sat", "UNPAID", null, 0, 0);
            mint.Status = HttpStatusCode.BadRequest;

            var firstPoll = await service.CheckAndRecordPaymentAsync(invalidQuoteId);
            await ctx.Entry(invalidQuote).ReloadAsync();
            Assert.Equal("Mint returned HTTP 400", firstPoll.Error);
            Assert.Equal("FAILED", invalidQuote.SettlementState);
            Assert.True(invalidQuote.NeedsManualReview);
            Assert.Equal("mint_poll_error", invalidQuote.FailureReasonCode);
            var callsAfterFailure = mint.Calls;

            var repeatedPoll = await service.CheckAndRecordPaymentAsync(invalidQuoteId);
            Assert.Equal("Mint returned HTTP 400", repeatedPoll.Error);
            Assert.Equal(callsAfterFailure, mint.Calls);

            // Upgrade-era rows have the failure code but were created before the
            // manual-review flag was set. Both checkout and background retry paths
            // must still leave these terminal quote-poll failures alone.
            invalidQuote.NeedsManualReview = false;
            invalidQuote.RetryCount = 0;
            invalidQuote.MintedProofsJson = "preserve-recovery-evidence";
            var errorBeforeLegacyPoll = invalidQuote.SettlementError;
            await ctx.SaveChangesAsync();
            var legacyRowPoll = await service.CheckAndRecordPaymentAsync(invalidQuoteId);
            Assert.Equal("Mint returned HTTP 400", legacyRowPoll.Error);
            var automaticRetry = await service.RetrySettlementAutomaticallyAsync("store", invalidQuoteId);
            Assert.False(automaticRetry.Settled);
            Assert.Equal(callsAfterFailure, mint.Calls);
            await ctx.Entry(invalidQuote).ReloadAsync();
            Assert.Equal("FAILED", invalidQuote.SettlementState);
            Assert.Equal(errorBeforeLegacyPoll, invalidQuote.SettlementError);
            Assert.True(invalidQuote.NeedsManualReview);
            Assert.Equal("preserve-recovery-evidence", invalidQuote.MintedProofsJson);
            Assert.Equal(0, invalidQuote.RetryCount);

            // The explicit merchant action clears the terminal hold and rechecks the same quote.
            invalidQuote.MintedProofsJson = null;
            await ctx.SaveChangesAsync();
            await service.RetrySettlementAsync("store", invalidQuoteId);
            await ctx.Entry(invalidQuote).ReloadAsync();
            Assert.Equal(callsAfterFailure + 1, mint.Calls);
            Assert.Equal("FAILED", invalidQuote.SettlementState);
            Assert.True(invalidQuote.NeedsManualReview);
        }
        finally
        {
            await using var drop = new Npgsql.NpgsqlCommand("DROP DATABASE \"" + database + "\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
