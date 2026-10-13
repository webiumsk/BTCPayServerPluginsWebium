using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.CashuMelt.Controllers;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Plugins.TestFixtures;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

[Collection("Cashu database")]
public class CashuMeltCheckoutIntegrationTests
{
    private sealed class CheckoutMint : HttpMessageHandler, IHttpClientFactory
    {
        public readonly string PrivateQuoteId = "private-" + Guid.NewGuid().ToString("N");
        public bool RestoreSupported = true;
        public bool WrongQuoteAmount, QuoteUnavailable;
        public bool WrongInvoiceAmount, InvalidInvoice, InvalidInfoJson, InfoTimeout;
        public HttpStatusCode InfoStatus = HttpStatusCode.OK;
        public string? CancelPhase;
        public Action? Cancel;
        public bool Funded;
        public bool LoseMeltResponse;
        public int QuoteCalls, MintCalls, MeltCalls;
        private bool _meltPaid;
        private long _amount, _payoutAmount;
        private string _fundingInvoice = "", _payoutInvoice = "", _preimage = "";
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object response;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/info")
            {
                if (CancelPhase == "info") { Cancel!(); throw new OperationCanceledException(ct); }
                if (InfoTimeout) throw new TaskCanceledException("Simulated unrequested HTTP timeout");
                if (InfoStatus != HttpStatusCode.OK) return new HttpResponseMessage(InfoStatus);
                if (InvalidInfoJson) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") };
                response = new { nuts = new Dictionary<string, object> { ["9"] = new { supported = RestoreSupported } } };
            }
            else if (path == "/v1/mint/quote/bolt11" && request.Method == HttpMethod.Post)
            {
                if (CancelPhase == "mint") { Cancel!(); throw new OperationCanceledException(ct); }
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                _amount = body.RootElement.GetProperty("amount").GetInt64();
                (_fundingInvoice, _) = LightningInvoiceFixture.Create(_amount + (WrongInvoiceAmount ? 1 : 0));
                if (InvalidInvoice) _fundingInvoice = "not-a-Lightning-invoice";
                QuoteCalls++;
                response = FundingQuote();
            }
            else if (path == "/v1/mint/quote/bolt11/" + PrivateQuoteId)
            {
                if (QuoteUnavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response = FundingQuote();
            }
            else if (path == "/v1/keys")
            {
                var keys = Enumerable.Range(0, 12).ToDictionary(i => (1L << i).ToString(),
                    _ => "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");
                response = new MintKeysResponse([new MintKeyset("009a1f293253e41e", "sat", keys)]);
            }
            else if (path == "/v1/keysets")
                response = new KeysetsInfoResponse([new KeysetInfo("009a1f293253e41e", "sat", true, 0)]);
            else if (path == "/v1/mint/bolt11")
            {
                var body = JsonSerializer.Deserialize<MintTokensRequest>(await request.Content!.ReadAsStringAsync(ct))!;
                Assert.Equal(PrivateQuoteId, body.Quote); Assert.True(Funded); Assert.Equal(0, MintCalls);
                MintCalls++;
                response = new MintTokensResponse(body.Outputs.Select(o => new BlindSignature(o.Amount, o.Id, o.B_)).ToArray());
            }
            else if (path == "/.well-known/lnurlp/merchant")
            {
                Assert.Equal("linky.example", request.RequestUri.Host);
                response = new { callback = "https://linky.example/callback", minSendable = 1000, maxSendable = 10000000 };
            }
            else if (path == "/callback")
            {
                _payoutAmount = long.Parse(request.RequestUri.Query.Split('=')[1]) / 1000;
                (_payoutInvoice, _preimage) = LightningInvoiceFixture.Create(_payoutAmount);
                response = new { pr = _payoutInvoice };
            }
            else if (path == "/v1/melt/quote/bolt11" && request.Method == HttpMethod.Post)
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal(_payoutInvoice, body.RootElement.GetProperty("request").GetString());
                response = PayoutQuote();
            }
            else if (path == "/v1/melt/quote/bolt11/payout")
                response = PayoutQuote();
            else if (path == "/v1/melt/bolt11")
            {
                var body = JsonSerializer.Deserialize<MeltTokensRequest>(await request.Content!.ReadAsStringAsync(ct))!;
                Assert.Equal("payout", body.Quote); Assert.Equal(0, MeltCalls);
                Assert.Equal(_amount, body.Inputs.Sum(p => p.Amount));
                _meltPaid = true; MeltCalls++;
                if (LoseMeltResponse) throw new HttpRequestException("Lost response after mint paid the original payout");
                response = new MeltTokensResponse(true, null, "PAID", _preimage, []);
            }
            else throw new InvalidOperationException("Unexpected fake request: " + request.Method + " " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }), Encoding.UTF8, "application/json") };
        }
        private MintQuoteBolt11Response FundingQuote() => new(PrivateQuoteId, _fundingInvoice,
            _amount + (WrongQuoteAmount ? 1 : 0), "sat", MintCalls > 0 ? "ISSUED" : Funded ? "PAID" : "UNPAID", null,
            Funded ? _amount : 0, MintCalls > 0 ? _amount : 0);
        private MeltQuoteResponse PayoutQuote() => new("payout", _payoutAmount, _amount - _payoutAmount,
            _meltPaid ? "PAID" : "UNPAID", null, _meltPaid ? _preimage : null, [], _payoutInvoice);
    }

    private static ServiceProvider Services(CashuMeltDbContextFactory factory, ApplicationDbContextFactory coreFactory,
        CheckoutMint mint, CashuMeltAssetProtector assets)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton(factory); services.AddSingleton(coreFactory);
        services.AddSingleton(new CashuMeltConfigService(factory)); services.AddSingleton(assets);
        services.AddSingleton(new CashuMeltMintClient(mint.CreateClient("mint"), NullLogger<CashuMeltMintClient>.Instance));
        services.AddSingleton<IHttpClientFactory>(mint);
        services.AddSingleton(new EventAggregator(new Logs())); services.AddSingleton<InvoiceRepository>();
        services.AddSingleton<PaymentService>(); services.AddSingleton<PaymentMethodHandlerDictionary>();
        var pluginServices = new ServiceCollection(); new CashuMeltPlugin().Execute(pluginServices);
        foreach (var registration in pluginServices.Where(r => r.ServiceType == typeof(CashuMeltQuoteService)
                     || r.ServiceType == typeof(CashuMeltPaymentService) || r.ServiceType == typeof(CashuMeltMintIssuance)
                     || r.ServiceType == typeof(IPaymentMethodHandler))) services.Add(registration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [CashuDatabaseFact]
    public async Task Real_checkout_creates_frozen_opaque_quote_and_records_payment_once_even_after_lost_melt_response()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION"));
        Assert.Equal("cashu_safety", connection.Database);
        var database = "cashu_safety_" + Guid.NewGuid().ToString("N");
        await using var admin = new Npgsql.NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        await using (var create = new Npgsql.NpgsqlCommand("CREATE DATABASE \"" + database + "\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            var options = Options.Create(new DatabaseOptions { ConnectionString = connection.ConnectionString });
            var coreFactory = new ApplicationDbContextFactory(options, NullLoggerFactory.Instance);
            await using var core = coreFactory.CreateContext(); await core.Database.EnsureCreatedAsync();
            var factory = new CashuMeltDbContextFactory(options);
            await using var ctx = factory.CreateContext(); await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
            foreach (var scenario in new[] { (LostResponse: false, Legacy: false), (LostResponse: true, Legacy: false), (LostResponse: false, Legacy: true) })
            {
                var mint = new CheckoutMint { LoseMeltResponse = scenario.LostResponse };
                var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
                using var provider = Services(factory, coreFactory, mint, assets);
                var config = provider.GetRequiredService<CashuMeltConfigService>();
                var settings = new CashuMeltStoreSettings { StoreId = Guid.NewGuid().ToString("N"), MintUrl = "https://mint.example", LightningAddress = "merchant@linky.example", Enabled = true };
                await config.SaveSettingsAsync(settings);
                var store = new StoreData { Id = settings.StoreId, StoreName = "Checkout regression" };
                core.Stores.Add(store);
                var invoice = new InvoiceEntity { Id = Guid.NewGuid().ToString("N"), StoreId = store.Id, Currency = "BTC", Price = 0.00000296m,
                    InvoiceTime = DateTimeOffset.UtcNow, ExpirationTime = DateTimeOffset.UtcNow.AddHours(1), MonitoringExpiration = DateTimeOffset.UtcNow.AddDays(1) };
                var handler = provider.GetRequiredService<PaymentMethodHandlerDictionary>()[CashuMeltPlugin.CashuMeltPaymentMethodId];
                invoice.UpdateTotals();
                var invoiceLogs = new InvoiceLogs();
                var context = new PaymentMethodContext(store, new StoreBlob(), new JObject(), handler, invoice, invoiceLogs, provider.GetRequiredService<InvoiceRepository>());
                await context.BeforeFetchingRates(); await context.CreatePaymentPrompt(checkPaymentMethodConflicts: false);
                Assert.True(context.Status == PaymentMethodContext.ContextStatus.Created, string.Join("\n", invoiceLogs.ToList()));
                invoice.SetPaymentPrompt(handler.PaymentMethodId, context.Prompt);
                var data = new InvoiceData { Id = invoice.Id, StoreDataId = store.Id, Status = "New" }; data.SetBlob(invoice);
                core.Invoices.Add(data); await core.SaveChangesAsync();
                var details = Assert.IsType<CashuMeltPromptDetails>(handler.ParsePaymentPromptDetails(context.Prompt.Details));
                Assert.NotEqual(mint.PrivateQuoteId, details.QuoteId);
                Assert.DoesNotContain(mint.PrivateQuoteId, context.Prompt.Details.ToString());
                Assert.Equal(details.QuoteId, context.Prompt.Destination); Assert.Contains(details.QuoteId, context.TrackedDestinations);
                var row = await ctx.CashuMeltPaymentRequests.SingleAsync(r => r.QuoteId == details.QuoteId);
                Assert.Equal(296, row.AmountSats); Assert.Equal("https://mint.example", row.MintUrl);
                Assert.NotEqual(mint.PrivateQuoteId, row.MintQuoteId); Assert.Equal(mint.PrivateQuoteId, assets.Unprotect(row.MintQuoteId!));
                Assert.Equal("merchant@linky.example", JsonSerializer.Deserialize<CashuMeltStoreSettings>(row.SettingsSnapshotJson!)!.LightningAddress);
                if (scenario.Legacy)
                {
                    // Reproduce a funded checkout row emitted by 1.4.0's old handler.
                    ctx.CashuMeltPaymentRequests.Remove(row); await ctx.SaveChangesAsync();
                    row = new CashuMeltPaymentRequest { QuoteId = mint.PrivateQuoteId, InvoiceId = invoice.Id,
                        StoreId = store.Id, AmountSats = details.AmountSats, Bolt11Invoice = details.Bolt11Invoice, CreatedAt = DateTimeOffset.UtcNow };
                    ctx.CashuMeltPaymentRequests.Add(row); await ctx.SaveChangesAsync();
                    details.QuoteId = row.QuoteId;
                    context.Prompt.Details = JObject.FromObject(details, handler.Serializer);
                    context.Prompt.Destination = row.QuoteId;
                    invoice.SetPaymentPrompt(handler.PaymentMethodId, context.Prompt); data.SetBlob(invoice); await core.SaveChangesAsync();
                }
                settings.Enabled = false; await config.SaveSettingsAsync(settings); // Disable new payments without stranding this one.
                mint.Funded = true;
                var payments = provider.GetRequiredService<CashuMeltPaymentService>();
                var result = await payments.CheckAndRecordPaymentAsync(row.QuoteId);
                if (scenario.Legacy)
                {
                    Assert.False(result.Paid); Assert.Contains("Original mint and payout configuration", result.Error);
                    Assert.Equal(0, mint.MintCalls); Assert.Equal(0, mint.MeltCalls);
                    var api = new CashuMeltApiController(config, factory, payments, null!, null!, null!);
                    async Task<Microsoft.AspNetCore.Mvc.IActionResult> Confirm(string requestedStore, bool consent)
                    {
                        api.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
                        { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
                        api.Request.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                        { mintUrl = settings.MintUrl, lightningAddress = "merchant@linky.example", confirmedOriginal = consent })));
                        return await api.ConfirmOriginalSettings(requestedStore, row.QuoteId);
                    }
                    Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await Confirm(store.Id, false));
                    Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(await Confirm("different-store", true));
                    mint.QuoteUnavailable = true;
                    var unavailable = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await Confirm(store.Id, true));
                    Assert.Equal(503, unavailable.StatusCode);
                    await ctx.Entry(row).ReloadAsync(); Assert.Null(row.SettingsSnapshotJson);
                    Assert.Equal("MANUAL_REVIEW", row.SettlementState);
                    mint.QuoteUnavailable = false;
                    mint.WrongQuoteAmount = true;
                    Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await Confirm(store.Id, true));
                    await ctx.Entry(row).ReloadAsync(); Assert.Null(row.SettingsSnapshotJson);
                    Assert.Equal(0, mint.MintCalls); Assert.Equal(0, mint.MeltCalls);
                    mint.WrongQuoteAmount = false;
                    var before = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await api.GetPayment(store.Id, row.QuoteId));
                    Assert.True(Assert.IsType<CashuMeltApiController.CashuMeltPaymentResponse>(before.Value).OriginalSettingsRequired);
                    Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await Confirm(store.Id, true));
                    await ctx.Entry(row).ReloadAsync(); Assert.Equal("MANUAL_REVIEW", row.SettlementState);
                    var checkBeforeRetry = await payments.CheckAndRecordPaymentAsync(row.QuoteId);
                    Assert.False(checkBeforeRetry.Paid); Assert.Contains("use Check / retry payment", checkBeforeRetry.Error);
                    Assert.Equal(0, mint.MintCalls); Assert.Equal(0, mint.MeltCalls);
                    await ctx.Entry(row).ReloadAsync(); Assert.Equal("MANUAL_REVIEW", row.SettlementState);
                    var after = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await api.GetPayment(store.Id, row.QuoteId));
                    Assert.False(Assert.IsType<CashuMeltApiController.CashuMeltPaymentResponse>(after.Value).OriginalSettingsRequired);
                    Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await Confirm(store.Id, true));
                    Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await api.RetryPayment(store.Id, row.QuoteId));
                    result = await payments.CheckAndRecordPaymentAsync(row.QuoteId);
                    Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await Confirm(store.Id, true));
                }
                if (scenario.LostResponse) { Assert.False(result.Paid); Assert.Null(result.Error); result = await payments.CheckAndRecordPaymentAsync(row.QuoteId); }
                Assert.True(result.Paid, result.Error); Assert.Null(result.Error);
                Assert.True((await payments.CheckAndRecordPaymentAsync(row.QuoteId)).Paid);
                await ctx.Entry(row).ReloadAsync(); Assert.Equal("SETTLED", row.SettlementState);
                Assert.Equal(1, mint.QuoteCalls); Assert.Equal(1, mint.MintCalls); Assert.Equal(1, mint.MeltCalls);
                var recorded = Assert.Single((await provider.GetRequiredService<InvoiceRepository>().GetInvoice(invoice.Id)).GetPayments(false));
                Assert.Equal(row.QuoteId, recorded.Id); Assert.Equal(PaymentStatus.Settled, recorded.Status);
            }
        }
        finally
        {
            await using var drop = new Npgsql.NpgsqlCommand("DROP DATABASE \"" + database + "\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    [CashuDatabaseFact]
    public async Task Checkout_never_exposes_a_quote_that_lacks_restore_support_or_has_the_wrong_amount()
    {
        var options = Options.Create(new DatabaseOptions { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! });
        var factory = new CashuMeltDbContextFactory(options);
        await using var ctx = factory.CreateContext(); await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
        var config = new CashuMeltConfigService(factory);
        var settings = new CashuMeltStoreSettings { StoreId = Guid.NewGuid().ToString("N"), MintUrl = "https://mint.example", LightningAddress = "merchant@linky.example", Enabled = true };
        await config.SaveSettingsAsync(settings);
        foreach (var mint in new[] { new CheckoutMint { RestoreSupported = false }, new CheckoutMint { WrongQuoteAmount = true },
                     new CheckoutMint { InfoStatus = HttpStatusCode.ServiceUnavailable }, new CheckoutMint { InvalidInfoJson = true },
                     new CheckoutMint { InfoTimeout = true }, new CheckoutMint { WrongInvoiceAmount = true }, new CheckoutMint { InvalidInvoice = true } })
        {
            var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
            var quotes = new CashuMeltQuoteService(new CashuMeltMintClient(mint.CreateClient("mint"), NullLogger<CashuMeltMintClient>.Instance), config, factory, assets, NullLogger<CashuMeltQuoteService>.Instance);
            var handler = new CashuMeltPaymentMethodHandler(config, quotes, NullLogger<CashuMeltPaymentMethodHandler>.Instance);
            var invoice = new InvoiceEntity { Id = Guid.NewGuid().ToString("N"), StoreId = settings.StoreId, Currency = "BTC", Price = 0.00000296m };
            invoice.UpdateTotals();
            var invoiceLogs = new InvoiceLogs();
            var context = new PaymentMethodContext(new StoreData { Id = settings.StoreId }, new StoreBlob(), new JObject(), handler, invoice, invoiceLogs, null!);
            await context.BeforeFetchingRates(); await context.CreatePaymentPrompt(checkPaymentMethodConflicts: false);
            Assert.Equal(PaymentMethodContext.ContextStatus.Failed, context.Status);
            Assert.Null(context.Prompt.Destination); Assert.Null(context.Prompt.Details); Assert.Empty(context.TrackedDestinations);
            Assert.False(await ctx.CashuMeltPaymentRequests.AnyAsync(r => r.InvoiceId == invoice.Id));
            Assert.Contains(invoiceLogs.ToList(), log => log.Log.Contains("Payment method unavailable"));
            Assert.DoesNotContain(invoiceLogs.ToList(), log => log.Log.Contains("Unexpected exception"));
        }
    }
    [CashuDatabaseFact]
    public async Task Requested_cancellation_during_recovery_probe_or_quote_creation_is_never_mapped_to_an_error_tuple()
    {
        var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions
        { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));
        await using var ctx = factory.CreateContext(); await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
        var config = new CashuMeltConfigService(factory);
        var settings = new CashuMeltStoreSettings { StoreId = Guid.NewGuid().ToString("N"), MintUrl = "https://mint.example", LightningAddress = "merchant@linky.example", Enabled = true };
        await config.SaveSettingsAsync(settings);
        foreach (var phase in new[] { "info", "mint" })
        {
            using var cancel = new CancellationTokenSource();
            var mint = new CheckoutMint { CancelPhase = phase, Cancel = cancel.Cancel };
            var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
            var quotes = new CashuMeltQuoteService(new CashuMeltMintClient(mint.CreateClient("mint"), NullLogger<CashuMeltMintClient>.Instance),
                config, factory, assets, NullLogger<CashuMeltQuoteService>.Instance);
            var invoiceId = Guid.NewGuid().ToString("N");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quotes.CreateQuoteAsync(invoiceId, settings.StoreId, 296, "sat", cancel.Token));
            Assert.False(await ctx.CashuMeltPaymentRequests.AnyAsync(r => r.InvoiceId == invoiceId));
        }
    }

}
