using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Plugins.SatfluxTickets.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class TicketSalesPresentationTests
{
    [Theory]
    [InlineData(null, "sk-SK,sk;q=0.9,en;q=0.8", "sk")]
    [InlineData(null, "es-419;q=0.9,de;q=0.7", "es")]
    [InlineData(null, "fr-FR,de-DE;q=0.9,en;q=0.8", "de")]
    [InlineData(null, "en;q=0,cs-CZ;q=0.6", "cs")]
    [InlineData(null, "en;q=0.2,sk;q=0.9", "sk")]
    [InlineData(null, "de;q=1.1,sk;q=NaN,cs;q=-1,es;q=0.5", "es")]
    [InlineData(null, "sk;q=0,de;q=0", "en")]
    [InlineData(null, "sk;q=bad,de;q=0.5", "de")]
    [InlineData(null, "sk;q=0.9;extra=x,cs;q=0.8", "cs")]
    [InlineData(null, "*", "en")]
    [InlineData("cs-CZ", "sk", "cs")]
    [InlineData("SK_sk", "en", "sk")]
    [InlineData("de", "sk", "de")]
    [InlineData("es", "sk", "es")]
    [InlineData("en", "sk", "en")]
    [InlineData("fr", "sk", "sk")]
    [InlineData("sk,en", "de", "de")]
    [InlineData("<script>alert(1)</script>", "sk", "sk")]
    [InlineData("../../sk", "cs", "cs")]
    [InlineData("sk\r\nX-Test: yes", "es", "es")]
    public void Language_is_bounded_allowlisted_and_quality_weighted(string? requested, string? header, string expected) =>
        Assert.Equal(expected, TicketSalesText.ResolveLanguage(requested!, header!));

    [Fact]
    public void Oversized_inputs_and_host_invariant_culture_do_not_throw_or_become_authority()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Assert.Equal("sk", TicketSalesText.ResolveLanguage(null!, "sk-SK,en;q=0.5"));
            Assert.Equal("en", TicketSalesText.ResolveLanguage(new string('x', 5000), new string('s', 5000)));
            Assert.Equal("en", TicketSalesText.ResolveLanguage(null!, string.Join(',', Enumerable.Repeat("fr", 32)) + ",sk"));
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Fact]
    public void No_javascript_fallback_preserves_explicit_UTC_instant_and_localized_copy()
    {
        var text = new TicketSalesText("sk");
        var date = new DateTime(2026, 11, 28, 8, 0, 0, DateTimeKind.Utc);
        Assert.EndsWith(" UTC", text.Date(date));
        Assert.Contains("8:00", text.Date(date));
        Assert.Equal("Tvoje časové pásmo", text["YourTimezone"]);
        Assert.Equal(8, date.Hour);
    }

    [Fact]
    public async Task Presentation_post_rejects_missing_session_before_accessing_any_store_or_payment_service()
    {
        // All repositories/payment services are deliberately null: rejection must precede their use.
        var controller = new UITicketSalesPublicController(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        var context = new DefaultHttpContext { Session = new EmptySession() };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        Assert.IsType<BadRequestResult>(await controller.ChangeLanguage("other-store", "event", new TicketSalesSubmission { TxnId = "unknown", Language = "sk" }));
        Assert.IsType<BadRequestResult>(await controller.ChangeLanguage("store", "event", new TicketSalesSubmission { Language = new string('s', 36) }));
        var action = typeof(UITicketSalesPublicController).GetMethod(nameof(UITicketSalesPublicController.ChangeLanguage))!;
        Assert.NotNull(action.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), false).SingleOrDefault());
        var sizeLimit = Assert.IsAssignableFrom<IRequestSizeLimitMetadata>(action.GetCustomAttributes(typeof(RequestSizeLimitAttribute), false).Single());
        Assert.Equal(131072L, sizeLimit.MaxRequestBodySize);
    }

    [Fact]
    public void Client_translations_are_complete_allowlisted_and_public_text_only()
    {
        var translations = TicketSalesText.Translations();
        Assert.Equal(new[] { "cs", "de", "en", "es", "sk" }, translations.Keys.Order());
        foreach (var copy in translations.Values)
        {
            Assert.Equal(translations["en"].Keys.Order(), copy.Keys.Order());
            Assert.All(copy, pair => { Assert.False(string.IsNullOrWhiteSpace(pair.Value)); Assert.DoesNotContain("<", pair.Value); });
        }
        var encoded = JsonSerializer.Serialize(translations);
        Assert.DoesNotContain("</script", encoded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Versioned_assets_match_actual_embedded_bytes_and_bypass_the_old_unversioned_script_identity()
    {
        var controller = new UITicketSalesPublicController(null!, null!, null!, new EmailService(null!, null!), null!, null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        foreach (var style in new[] { false, true })
        {
            var version = style ? TicketSalesText.StyleVersion : TicketSalesText.ScriptVersion;
            Assert.Matches("^[a-f0-9]{64}$", version);
            var result = Assert.IsType<ContentResult>(style ? controller.SalesStyleVersioned(version) : controller.SalesScriptVersioned(version));
            Assert.Equal(version, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Content!))).ToLowerInvariant());
            Assert.Equal("no-cache, max-age=0, must-revalidate", controller.Response.Headers.CacheControl.ToString());
            Assert.Equal("\"" + version + "\"", controller.Response.Headers.ETag.ToString());
            Assert.NotEqual("ff9fcac8cb1dfb629c6d6e8b2127b5526451c07abbc901c03b9467fa8626c726", version); // 2.1.0 JS
        }
        Assert.IsType<ContentResult>(controller.SalesScript());
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.IsType<ContentResult>(controller.SalesStyle());
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../private")]
    [InlineData("2.1.0")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void Invalid_asset_versions_fail_closed_without_accessing_resource_or_payment_services(string? version)
    {
        var controller = new UITicketSalesPublicController(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        Assert.IsType<NotFoundResult>(controller.SalesScriptVersioned(version!));
        Assert.IsType<NotFoundResult>(controller.SalesStyleVersioned(version!));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    private sealed class EmptySession : ISession
    {
        public bool IsAvailable => true;
        public string Id => "synthetic";
        public IEnumerable<string> Keys => Array.Empty<string>();
        public void Clear() => throw new InvalidOperationException("Presentation rejection must not write session");
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => throw new InvalidOperationException("Presentation rejection must not write session");
        public void Set(string key, byte[] value) => throw new InvalidOperationException("Presentation rejection must not write session");
        public bool TryGetValue(string key, out byte[] value) { value = null!; return false; }
    }
}
