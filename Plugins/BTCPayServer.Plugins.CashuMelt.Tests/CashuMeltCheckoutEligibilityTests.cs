using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.PaymentHandler;
using BTCPayServer.Plugins.CashuMelt.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

[Collection("Cashu database")]
public class CashuMeltCheckoutEligibilityTests
{
    private sealed class LightningConfigHandler : IPaymentMethodHandler
    {
        public PaymentMethodId PaymentMethodId => PaymentTypes.LN.GetPaymentMethodId("BTC");
        public Newtonsoft.Json.JsonSerializer Serializer { get; } = new();
        public Task BeforeFetchingRates(PaymentMethodContext context) { context.Prompt.Currency = "BTC"; return Task.CompletedTask; }
        public Task ConfigurePrompt(PaymentMethodContext context) { context.Prompt.Destination = "synthetic-ln-invoice"; return Task.CompletedTask; }
        public object ParsePaymentMethodConfig(JToken config) => config.ToObject<LightningPaymentMethodConfig>()!;
        public object ParsePaymentPromptDetails(JToken details) => details;
        public object ParsePaymentDetails(JToken details) => details;
    }

    [Fact]
    public void Plugin_does_not_register_the_old_redirect_that_would_override_a_materialized_Cashu_selection()
    {
        var services = new ServiceCollection(); new CashuMeltPlugin().Execute(services);
        var extensions = services.Where(d => d.ServiceType == typeof(IUIExtension))
            .Select(d => Assert.IsAssignableFrom<IUIExtension>(d.ImplementationInstance)).ToArray();
        Assert.DoesNotContain(extensions, extension => extension.Partial.Contains("CashuPreferLightningRedirect", StringComparison.Ordinal));
        Assert.Contains(extensions, extension => extension.Partial == "CashuMelt/CashuMeltCheckoutExtension" && extension.Location == "checkout-end");
    }

    [CashuDatabaseFact]
    public async Task Whole_invoice_creation_keeps_Lightning_when_only_Cashu_is_suppressed()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION"));
        Assert.Equal("cashu_safety", connection.Database);
        var database = "cashu_safety_" + Guid.NewGuid().ToString("N");
        await using var admin = new Npgsql.NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        await using (var create = new Npgsql.NpgsqlCommand("CREATE DATABASE \"" + database + "\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            var coreFactory = new ApplicationDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connection.ConnectionString }), NullLoggerFactory.Instance);
            await using var core = coreFactory.CreateContext(); await core.Database.EnsureCreatedAsync();
            var store = CashuMeltLazyFallbackTests.Store(withLn: true);
            store.SetPaymentMethodConfig(CashuMeltPlugin.CashuMeltPaymentMethodId, new JObject());
            var cashu = new CashuMeltPaymentMethodHandler(null!, null!, NullLogger<CashuMeltPaymentMethodHandler>.Instance);
            var invoice = new InvoiceEntity { Id = "synthetic-invoice", StoreId = store.Id, Currency = "BTC" };
            var creation = new InvoiceCreationContext(store, new StoreBlob(), invoice, new InvoiceLogs(),
                new PaymentMethodHandlerDictionary([new LightningConfigHandler(), cashu]), null, new InvoiceRepository(coreFactory, null!));
            await creation.BeforeFetchingRates(); await creation.CreatePaymentPrompts();
            Assert.Equal(PaymentMethodContext.ContextStatus.Created, creation.PaymentMethodContexts[PaymentTypes.LN.GetPaymentMethodId("BTC")].Status);
            Assert.Equal(PaymentMethodContext.ContextStatus.Failed, creation.PaymentMethodContexts[CashuMeltPlugin.CashuMeltPaymentMethodId].Status);
        }
        finally
        {
            await using var drop = new Npgsql.NpgsqlCommand("DROP DATABASE \"" + database + "\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("type=lnaddress;ln-address=merchant@example.com")]
    [InlineData("type=blitz;ln-address=merchant")]
    [InlineData("type=flash;ln-address=merchant")]
    [InlineData("type=blink;username=merchant")]
    public void Receive_only_address_is_recognized_without_claiming_spending_capability(string connection)
    {
        var store = new StoreData();
        store.SetPaymentMethodConfig(PaymentTypes.LN.GetPaymentMethodId("BTC"), new JObject { ["connectionString"] = connection });
        var backend = new StoreLightningBackendService(new PaymentMethodHandlerDictionary([new LightningConfigHandler()])).Detect(store);
        Assert.Equal(StoreLightningBackendType.LightningAddressReceiver, backend.BackendType);
        Assert.False(backend.CanAttemptPayout);
        Assert.False(new ExternalLightningSettlementProvider(null!, null!, null!, NullLogger<ExternalLightningSettlementProvider>.Instance).Supports(backend));
        Assert.DoesNotContain("Unsupported", backend.Description);
    }

    [Fact]
    public void API_Blink_keeps_its_existing_outbound_adapter_classification()
    {
        var store = new StoreData();
        store.SetPaymentMethodConfig(PaymentTypes.LN.GetPaymentMethodId("BTC"), new JObject { ["connectionString"] = "type=blink;api-key=synthetic;wallet-id=synthetic" });
        var backend = new StoreLightningBackendService(new PaymentMethodHandlerDictionary([new LightningConfigHandler()])).Detect(store);
        Assert.Equal(StoreLightningBackendType.Blink, backend.BackendType); Assert.True(backend.CanAttemptPayout);
    }

    [CashuDatabaseFact]
    public async Task Materialized_quote_is_reused_with_disabled_settings_and_new_Lightning_without_touching_original_evidence()
    {
        var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));
        await using var db = factory.CreateContext(); await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(db);
        var store = CashuMeltLazyFallbackTests.Store(withLn: true); store.Id = Guid.NewGuid().ToString("N");
        var quoteId = Guid.NewGuid().ToString("N"); var invoice = new InvoiceEntity { Id = Guid.NewGuid().ToString("N"), StoreId = store.Id, Currency = "BTC" };
        var row = new CashuMeltPaymentRequest { QuoteId = quoteId, InvoiceId = invoice.Id, StoreId = store.Id,
            AmountSats = 123, Unit = "sat", Bolt11Invoice = "original-synthetic-invoice", SettingsSnapshotJson = "original-snapshot-marker",
            MintUrl = "https://original-mint.example", MintedProofsJson = "retained-proof-marker", SettlementState = "FAILED", SettlementError = "Mint returned HTTP 400" };
        db.CashuMeltPaymentRequests.Add(row);
        db.CashuMeltStoreSettings.Add(new CashuMeltStoreSettings { StoreId = store.Id, Enabled = false, MintUrl = "https://different.example", LightningAddress = "different@example.com" });
        await db.SaveChangesAsync();
        var config = new CashuMeltConfigService(factory);
        var quotes = new CashuMeltQuoteService(null!, config, factory, null!, NullLogger<CashuMeltQuoteService>.Instance);
        var handler = new CashuMeltPaymentMethodHandler(config, quotes, NullLogger<CashuMeltPaymentMethodHandler>.Instance);
        invoice.SetPaymentPrompt(handler.PaymentMethodId, new PaymentPrompt { PaymentMethodId = handler.PaymentMethodId, Destination = quoteId,
            Details = JObject.FromObject(new CashuMeltPromptDetails { QuoteId = quoteId, Bolt11Invoice = "displayed-old", AmountSats = 999 }) });
        var context = new PaymentMethodContext(store, new StoreBlob(), new JObject(), handler, invoice, new InvoiceLogs(), null!);
        await context.BeforeFetchingRates(); await context.CreatePaymentPrompt(checkPaymentMethodConflicts: false);
        Assert.Equal(PaymentMethodContext.ContextStatus.Created, context.Status);
        Assert.Equal(quoteId, context.Prompt.Destination);
        var details = Assert.IsType<CashuMeltPromptDetails>(handler.ParsePaymentPromptDetails(context.Prompt.Details));
        Assert.Equal(123, details.AmountSats); Assert.Equal("original-synthetic-invoice", details.Bolt11Invoice);
        await db.Entry(row).ReloadAsync();
        Assert.Equal("original-snapshot-marker", row.SettingsSnapshotJson); Assert.Equal("retained-proof-marker", row.MintedProofsJson);
        Assert.Equal("FAILED", row.SettlementState); Assert.Equal("Mint returned HTTP 400", row.SettlementError);
        Assert.Equal(1, await db.CashuMeltPaymentRequests.CountAsync(r => r.StoreId == store.Id));
        Assert.Equal((1, 0), await config.GetUnresolvedFundsAsync(store.Id, default));
        Assert.Null(await quotes.FindExistingPromptAsync("other-invoice", store.Id, quoteId));
        Assert.Null(await quotes.FindExistingPromptAsync(invoice.Id, "other-store", quoteId));
    }

    [CashuDatabaseFact]
    public async Task Forged_materialized_prompt_with_wrong_invoice_or_store_cannot_authorize_new_quote_or_reuse()
    {
        var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));
        await using var db = factory.CreateContext(); await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(db);
        var store = CashuMeltLazyFallbackTests.Store(withLn: true); store.Id = Guid.NewGuid().ToString("N");
        var quote = Guid.NewGuid().ToString("N");
        db.CashuMeltPaymentRequests.Add(new CashuMeltPaymentRequest { QuoteId = quote, InvoiceId = "actual-invoice", StoreId = store.Id, AmountSats = 10, Bolt11Invoice = "original" }); await db.SaveChangesAsync();
        var handler = new CashuMeltPaymentMethodHandler(null!, new CashuMeltQuoteService(null!, null!, factory, null!, NullLogger<CashuMeltQuoteService>.Instance), NullLogger<CashuMeltPaymentMethodHandler>.Instance);
        foreach (var invalidBinding in new[] { "invoice", "store", "details" })
        {
            var invoice = new InvoiceEntity { Id = invalidBinding == "invoice" ? "different-invoice" : "actual-invoice",
                StoreId = invalidBinding == "store" ? "foreign-store" : store.Id, Currency = "BTC" };
            invoice.SetPaymentPrompt(handler.PaymentMethodId, new PaymentPrompt { PaymentMethodId = handler.PaymentMethodId, Destination = quote,
                Details = JObject.FromObject(new CashuMeltPromptDetails { QuoteId = invalidBinding == "details" ? "different-quote" : quote, Bolt11Invoice = "forged" }) });
            var context = new PaymentMethodContext(store, new StoreBlob(), new JObject(), handler, invoice, new InvoiceLogs(), null!);
            await context.BeforeFetchingRates(); await context.CreatePaymentPrompt(checkPaymentMethodConflicts: false);
            Assert.Equal(PaymentMethodContext.ContextStatus.Failed, context.Status); Assert.Null(context.Prompt.Destination);
        }
    }
}
