using System.Net;
using System.Text;
using System.Text.Json;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.CashuMelt.Controllers;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

[Collection("Cashu database")]
public class CashuMeltChangeApiTests
{
    [Fact]
    public void All_change_routes_require_Greenfield_and_store_modify_policy_without_cookie_fallback()
    {
        var attributes = typeof(CashuMeltChangeApiController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().ToArray();
        Assert.Equal(AuthenticationSchemes.GreenfieldAPIKeys, Assert.Single(attributes, a => a.AuthenticationSchemes != null).AuthenticationSchemes);
        Assert.Equal(Policies.CanModifyStoreSettings, Assert.Single(attributes, a => a.Policy != null).Policy);
        Assert.Empty(typeof(CashuMeltChangeApiController).GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
        foreach (var name in new[] { "Export", "Verify" })
        {
            var consumes = Assert.Single(typeof(CashuMeltChangeApiController).GetMethod(name)!.GetCustomAttributes(typeof(ConsumesAttribute), true));
            Assert.Contains("application/json", Assert.IsType<ConsumesAttribute>(consumes).ContentTypes);
        }
    }

    private static CashuMeltChangeApiController Controller(CashuMeltPaymentService service, string json = "{\"confirmed\":true}", string contentType = "application/json")
    {
        var controller = new CashuMeltChangeApiController(service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.ContentType = contentType;
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return controller;
    }

    private static void Private(CashuMeltChangeApiController controller)
    {
        Assert.Equal("private, no-store, no-cache", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", controller.Response.Headers.Pragma.ToString());
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"confirmed\":false}")]
    [InlineData("{\"confirmed\":\"true\"}")]
    [InlineData("{\"confirmed\":1}")]
    [InlineData("{\"confirmed\":true,\"confirmed\":false}")]
    [InlineData("{\"confirmed\":true,\"confirmed\":true}")]
    [InlineData("{\"confirmed\":true,\"storeId\":\"other\"}")]
    public async Task Strict_confirmation_rejects_before_any_DB_or_network_access(string json)
    {
        var export = Controller(null!, json);
        Assert.IsType<BadRequestObjectResult>(await export.Export("store")); Private(export);
        var verify = Controller(null!, json);
        Assert.IsType<BadRequestObjectResult>(await verify.Verify("store")); Private(verify);
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("text/plain")]
    public async Task Non_JSON_confirmation_is_rejected(string contentType)
    {
        var controller = Controller(null!, contentType: contentType);
        Assert.IsType<BadRequestObjectResult>(await controller.Export("store"));
    }

    private sealed class Mint : HttpMessageHandler
    {
        public string State = "UNSPENT";
        public int Calls;
        public bool Throw;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/v1/checkstate", request.RequestUri!.AbsolutePath); // Never mint, melt or pay.
            if (Throw) throw new HttpRequestException("secret-marker transport body private-intent");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var states = body.RootElement.GetProperty("Ys").EnumerateArray().Select(y => new ProofStateInfo(y.GetString()!, State, null)).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new CheckStateResponse(states)), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CapturedLog : ILogger<CashuMeltPaymentService>
    {
        public List<string> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception) + exception);
    }

    private static async Task<(CashuMeltPaymentService Service, CashuMeltDbContextFactory Factory, CashuMeltAssetProtector Assets, CapturedLog Logs)> Setup(Mint mint)
    {
        var factory = new CashuMeltDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = Environment.GetEnvironmentVariable("CASHUMELT_TEST_CONNECTION")! }));
        await using var ctx = factory.CreateContext(); await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx);
        var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
        var client = new CashuMeltMintClient(new HttpClient(mint), NullLogger<CashuMeltMintClient>.Instance);
        var logs = new CapturedLog(); var config = new CashuMeltConfigService(factory);
        var service = new CashuMeltPaymentService(client, config, factory, null!, null!, null!, null!, null!, logs,
            new CashuMeltMintIssuance(client, assets), assets, new CashuMeltQuoteService(client, config, factory, assets, NullLogger<CashuMeltQuoteService>.Instance));
        return (service, factory, assets, logs);
    }

    private static CashuMeltChangeProof Row(string store, long amount = 1, string state = "AVAILABLE", string unit = "sat", string? intent = null) => new()
    {
        StoreId = store, MintUrl = "https://private-mint.example", Amount = amount, State = state, Unit = unit,
        KeysetId = "private-keyset", Secret = "secret-marker", C = Guid.NewGuid().ToString("N"), SweepIntentJson = intent, SourceQuoteId = "private-quote"
    };

    [CashuDatabaseFact]
    public async Task Summary_counts_small_change_unknown_units_and_terminal_intents_without_private_fields()
    {
        var setup = await Setup(new Mint()); var store = Guid.NewGuid().ToString("N");
        await using var ctx = setup.Factory.CreateContext();
        ctx.CashuMeltChangeProofs.AddRange(Row(store, 4), Row(store), Row(store, 8, "EXPORTED"), Row(store, 16, "SWEEPING"),
            Row(store, 32, "MANUAL_REVIEW"), Row(store, 64, "FUTURE_STATE"), Row(store, 128, "SWEPT", intent: "private-intent"),
            Row(store, 256, "RECOVERED", intent: "private-intent"), Row(store, 512, "AVAILABLE", "usd"),
            Row(store, 1024, "SWEPT"), Row(store, 2048, "RECOVERED"), Row("other-store", 4096));
        await ctx.SaveChangesAsync();
        var controller = Controller(setup.Service); var result = Assert.IsType<OkObjectResult>(await controller.Summary(store));
        var summary = Assert.IsType<CashuMeltChangeSummary>(result.Value);
        Assert.Equal(new CashuMeltChangeSummary(1, "61", "1152", "100", 9, 2, 1, 1, 1, 1, 3), summary);
        Assert.Equal((0, summary.UnresolvedRecords), await new CashuMeltConfigService(setup.Factory).GetUnresolvedFundsAsync(store, default));
        var json = JsonSerializer.Serialize(summary);
        foreach (var marker in new[] { "secret-marker", "private-", store, "other-store", "intent", "MintUrl" }) Assert.DoesNotContain(marker, json);
        Private(controller);
        Assert.Equal("0", (await setup.Service.GetChangeSummaryAsync("empty", default)).PendingSat);
    }

    [CashuDatabaseFact]
    public async Task Export_repeat_and_verification_reuse_services_and_never_release_remaining_holds()
    {
        var mint = new Mint(); var setup = await Setup(mint); var store = Guid.NewGuid().ToString("N");
        await using var ctx = setup.Factory.CreateContext();
        var available = Row(store, 5); available.Secret = setup.Assets.Protect(available.Secret);
        var unknown = Row(store, 2, "FUTURE_STATE"); var otherStore = Row("other-" + store, 4);
        ctx.CashuMeltChangeProofs.AddRange(available, unknown, otherStore); await ctx.SaveChangesAsync();
        var export = Controller(setup.Service); var result = Assert.IsType<OkObjectResult>(await export.Export(store)); Private(export);
        var tokens = JsonSerializer.SerializeToElement(result.Value).GetProperty("tokens"); Assert.Single(tokens.EnumerateArray());
        var repeat = Assert.IsType<OkObjectResult>(await Controller(setup.Service).Export(store));
        Assert.Equal(tokens.GetRawText(), JsonSerializer.SerializeToElement(repeat.Value).GetProperty("tokens").GetRawText());
        Assert.Equal(0, mint.Calls); Assert.Equal((0, 2), await new CashuMeltConfigService(setup.Factory).GetUnresolvedFundsAsync(store, default));
        await ctx.Entry(available).ReloadAsync(); Assert.Equal("EXPORTED", available.State); Assert.StartsWith("protected:", available.Secret);
        foreach (var state in new[] { "UNSPENT", "PENDING", "UNKNOWN" })
        {
            mint.State = state; var verify = Controller(setup.Service);
            Assert.IsType<ConflictObjectResult>(await verify.Verify(store)); Private(verify);
            await ctx.Entry(available).ReloadAsync(); Assert.Equal("EXPORTED", available.State);
        }
        mint.Throw = true; var failed = Assert.IsType<ConflictObjectResult>(await Controller(setup.Service).Verify(store));
        Assert.DoesNotContain("secret-marker", JsonSerializer.Serialize(failed.Value));
        Assert.DoesNotContain("secret-marker", string.Join("\n", setup.Logs.Entries));
        Assert.DoesNotContain("private-intent", string.Join("\n", setup.Logs.Entries));
        mint.Throw = false; mint.State = "SPENT";
        var verified = Assert.IsType<OkObjectResult>(await Controller(setup.Service).Verify(store));
        Assert.True(JsonSerializer.SerializeToElement(verified.Value).GetProperty("verified").GetBoolean());
        await ctx.Entry(available).ReloadAsync(); await ctx.Entry(unknown).ReloadAsync(); await ctx.Entry(otherStore).ReloadAsync();
        Assert.Equal("RECOVERED", available.State); Assert.Equal("consumed", available.Secret);
        Assert.Equal("FUTURE_STATE", unknown.State); Assert.Equal("AVAILABLE", otherStore.State);
        Assert.Equal((0, 1), await new CashuMeltConfigService(setup.Factory).GetUnresolvedFundsAsync(store, default));
    }

    [CashuDatabaseFact]
    public async Task Amounts_preserve_int64_precision_and_invalid_aggregates_are_unavailable_not_zero()
    {
        var setup = await Setup(new Mint()); var store = Guid.NewGuid().ToString("N");
        await using var ctx = setup.Factory.CreateContext(); var row = Row(store, long.MaxValue);
        ctx.CashuMeltChangeProofs.Add(row); await ctx.SaveChangesAsync();
        Assert.Equal("9223372036854775807", (await setup.Service.GetChangeSummaryAsync(store, default)).PendingSat);
        ctx.CashuMeltChangeProofs.Add(Row(store)); await ctx.SaveChangesAsync();
        var overflow = Controller(setup.Service); var result = Assert.IsType<ObjectResult>(await overflow.Summary(store));
        Assert.Equal(503, result.StatusCode); Private(overflow);
        Assert.DoesNotContain("secret-marker", JsonSerializer.Serialize(result.Value));
        row.Amount = -1; await ctx.SaveChangesAsync();
        Assert.Equal(503, Assert.IsType<ObjectResult>(await Controller(setup.Service).Summary(store)).StatusCode);
    }

    [CashuDatabaseFact]
    public async Task Empty_export_is_explicit_and_does_not_claim_wallet_safety()
    {
        var setup = await Setup(new Mint()); var export = Controller(setup.Service);
        var result = Assert.IsType<OkObjectResult>(await export.Export(Guid.NewGuid().ToString("N")));
        Assert.Empty(JsonSerializer.SerializeToElement(result.Value).GetProperty("tokens").EnumerateArray());
        Assert.DoesNotContain("safe", JsonSerializer.Serialize(result.Value)); Private(export);
    }
}
