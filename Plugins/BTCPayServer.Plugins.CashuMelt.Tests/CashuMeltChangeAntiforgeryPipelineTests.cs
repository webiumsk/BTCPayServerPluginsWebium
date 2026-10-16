using System.Net;
using BTCPayServer.Client;
using BTCPayServer.Filters;
using BTCPayServer.Plugins.CashuMelt.Controllers;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

// The actual host filter must continue protecting UI mutations.
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("cashu-test/native-change/{storeId}")]
public sealed class UICashuAntiforgeryControlController : Controller
{
    [HttpPost]
    public IActionResult Submit(string storeId) => Ok(new { accepted = true });
}

public class CashuMeltChangeAntiforgeryPipelineTests
{
    [Fact]
    public async Task Real_host_global_antiforgery_filter_protects_UI_but_does_not_reject_API_key_JSON_mutations()
    {
        var hostBuilder = new HostBuilder().ConfigureLogging(logging => logging.ClearProviders());
        hostBuilder.ConfigureWebHost(builder =>
        {
            builder.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddAntiforgery();
                services.AddTransient<IStartupFilter, CashuMeltChangePrivacyStartupFilter>();
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, CashuMeltChangeAuthorizationTests.SyntheticAuthentication>(AuthenticationSchemes.GreenfieldAPIKeys, _ => { })
                    .AddScheme<AuthenticationSchemeOptions, CashuMeltChangeAuthorizationTests.SyntheticAuthentication>(AuthenticationSchemes.Cookie, _ => { })
                    .AddScheme<AuthenticationSchemeOptions, CashuMeltChangeAuthorizationTests.SyntheticAuthentication>(AuthenticationSchemes.GreenfieldBasic, _ => { });
                services.AddAuthorization(options => options.AddPolicy(Policies.CanModifyStoreSettings, policy =>
                    policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.HasClaim("permission", "modify")
                        && context.Resource is Microsoft.AspNetCore.Http.HttpContext http
                        && context.User.HasClaim("store", http.Request.RouteValues["storeId"]?.ToString() ?? ""))));
                services.AddControllers(options => options.Filters.Add(new UIControllerAntiforgeryTokenAttribute()))
                    .AddApplicationPart(typeof(CashuMeltChangeApiController).Assembly)
                    .AddApplicationPart(typeof(UICashuAntiforgeryControlController).Assembly)
                    .AddControllersAsServices();
                // An authorized, confirmed API request reaches the action and returns its fixed
                // unavailable error. No database, wallet, mint or provider service is invoked.
                services.AddTransient<CashuMeltChangeApiController>(_ => new CashuMeltChangeApiController(null!));
            });
            builder.Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });
        });
        using var host = hostBuilder.Build(); await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        async Task<HttpResponseMessage> Post(string path, string json, string? scheme = AuthenticationSchemes.GreenfieldAPIKeys,
            string contentType = "application/json", string store = "store-a", string permission = "modify")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, contentType);
            if (scheme is not null) request.Headers.Add("X-Test-Scheme", scheme);
            request.Headers.Add("X-Test-Store", store); request.Headers.Add("X-Test-Permission", permission);
            return await client.SendAsync(request);
        }
        foreach (var action in new[] { "export", "verify" })
        {
            var path = "/api/v1/stores/store-a/plugins/cashumelt/change/" + action;
            using (var confirmed = await Post(path, "{\"confirmed\":true}"))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, confirmed.StatusCode);
                Assert.Contains("cashu_change_unavailable", await confirmed.Content.ReadAsStringAsync());
                Assert.True(confirmed.Headers.CacheControl!.NoStore); Assert.True(confirmed.Headers.CacheControl.Private);
                Assert.Equal("nosniff", Assert.Single(confirmed.Headers.GetValues("X-Content-Type-Options")));
            }
            foreach (var invalid in new[] { "{}", "{\"confirmed\":false}", "{\"confirmed\":\"true\"}", "{\"confirmed\":true,\"confirmed\":true}" })
            {
                using var response = await Post(path, invalid);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Contains("cashu_change_confirmation_required", await response.Content.ReadAsStringAsync());
                Assert.True(response.Headers.CacheControl!.NoStore);
            }
            foreach (var scheme in new[] { null, AuthenticationSchemes.Cookie, AuthenticationSchemes.GreenfieldBasic })
            {
                using var denied = await Post(path, "{\"confirmed\":true}", scheme);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); Assert.True(denied.Headers.CacheControl!.NoStore);
            }
            using var otherStore = await Post(path, "{\"confirmed\":true}", store: "store-b");
            Assert.Equal(HttpStatusCode.Forbidden, otherStore.StatusCode);
            using var missingPermission = await Post(path, "{\"confirmed\":true}", permission: "view");
            Assert.Equal(HttpStatusCode.Forbidden, missingPermission.StatusCode);
            using var wrongContentType = await Post(path, "{\"confirmed\":true}", contentType: "text/plain");
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContentType.StatusCode);
            Assert.True(wrongContentType.Headers.CacheControl!.NoStore);
        }
        using var native = await Post("/cashu-test/native-change/store-a", "{\"confirmed\":true}", AuthenticationSchemes.Cookie);
        Assert.Equal(HttpStatusCode.BadRequest, native.StatusCode); // Native UI still requires its CSRF token.
        await host.StopAsync();
    }
}
