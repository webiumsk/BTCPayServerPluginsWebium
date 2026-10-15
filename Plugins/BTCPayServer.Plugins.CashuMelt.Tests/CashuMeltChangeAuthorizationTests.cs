using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;
using BTCPayServer.Client;
using BTCPayServer.Plugins.CashuMelt.Controllers;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

/// <summary>
/// Exercises actual ASP.NET Core routing/authorization with synthetic authentication and
/// store-permission policy. Native BTCPay's permission handler is unchanged, not replaced in production.
/// </summary>
public class CashuMeltChangeAuthorizationTests
{
    public sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-Scheme"] != Scheme.Name) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new[] { new Claim("store", Request.Headers["X-Test-Store"].ToString()), new Claim("permission", Request.Headers["X-Test-Permission"].ToString()) };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    [Fact]
    public async Task Real_endpoint_pipeline_rejects_anonymous_cookie_wrong_store_and_missing_permission()
    {
        var hostBuilder = new HostBuilder().ConfigureLogging(logging => logging.ClearProviders());
        hostBuilder.ConfigureWebHost(builder =>
        {
        builder.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.ConfigureServices(services =>
        {
        services.AddTransient<IStartupFilter, CashuMeltChangePrivacyStartupFilter>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>(AuthenticationSchemes.GreenfieldAPIKeys, _ => { })
            .AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>(AuthenticationSchemes.GreenfieldBasic, _ => { })
            .AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>(AuthenticationSchemes.Cookie, _ => { });
        services.AddAuthorization(options => options.AddPolicy(Policies.CanModifyStoreSettings, policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context => context.User.HasClaim("permission", "modify")
                && context.Resource is Microsoft.AspNetCore.Http.HttpContext http
                && context.User.HasClaim("store", http.Request.RouteValues["storeId"]?.ToString() ?? ""))));
        services.AddControllers().AddApplicationPart(typeof(CashuMeltChangeApiController).Assembly).AddControllersAsServices();
        services.AddTransient<CashuMeltChangeApiController>(_ => new CashuMeltChangeApiController(null!));
        });
        builder.Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        });
        });
        using var app = hostBuilder.Build();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        foreach (var path in new[] { "", "/export", "/verify" })
        {
            async Task<HttpStatusCode> Request(string? scheme, string store = "store-a", string permission = "modify")
            {
                using var request = new HttpRequestMessage(path == "" ? HttpMethod.Get : HttpMethod.Post, "/api/v1/stores/store-a/plugins/cashumelt/change" + path);
                if (scheme != null) request.Headers.Add("X-Test-Scheme", scheme);
                request.Headers.Add("X-Test-Store", store); request.Headers.Add("X-Test-Permission", permission);
                if (path != "") request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Assert.True(response.Headers.CacheControl!.NoStore);
                Assert.True(response.Headers.CacheControl.Private);
                Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
                return response.StatusCode;
            }
            Assert.Equal(HttpStatusCode.Unauthorized, await Request(null));
            Assert.Equal(HttpStatusCode.Unauthorized, await Request(AuthenticationSchemes.Cookie));
            Assert.Equal(HttpStatusCode.Unauthorized, await Request(AuthenticationSchemes.GreenfieldBasic));
            Assert.Equal(HttpStatusCode.Forbidden, await Request(AuthenticationSchemes.GreenfieldAPIKeys, "store-b"));
            Assert.Equal(HttpStatusCode.Forbidden, await Request(AuthenticationSchemes.GreenfieldAPIKeys, permission: "view"));
            // Authorized control reaches only the action; invalid POST never invokes service.
            Assert.Equal(path == "" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest, await Request(AuthenticationSchemes.GreenfieldAPIKeys));
        }
        await app.StopAsync();
    }
}
