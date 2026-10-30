using System.Reflection;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using Ganss.Xss;
using Xunit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

internal static class HostRichTextPolicy
{
    internal static RichTextHtmlSanitizer Create()
    {
        // Exercise the exact target host's DI factory, not HtmlSanitizer's unrelated defaults.
        var factory = typeof(UIInvoiceController).Assembly.GetType("BTCPayServer.Hosting.HtmlSanitizerFactory", true)!;
        var create = factory.GetMethod("CreateRichText", BindingFlags.Static | BindingFlags.NonPublic)!;
        return new RichTextHtmlSanitizer((HtmlSanitizer)create.Invoke(null, null)!);
    }
}

public class HostRichTextPolicyTests
{
    [Fact]
    public void Description_preserves_supported_media_and_inline_formatting()
    {
        var service = new RaffleDescriptionHtml(HostRichTextPolicy.Create());
        var html = service.Sanitize("<p style='color:red;text-align:center'>Hello</p><img src='https://example.com/image.png'><video src='https://example.com/video.mp4' controls></video>")!;
        Assert.Contains("<img", html);
        Assert.Contains("img-fluid", html);
        Assert.Contains("<video", html);
        Assert.Contains("text-align", html);
        Assert.Contains("Hello", html);
    }

    [Fact]
    public void Legacy_content_is_safe_without_requiring_a_resave()
    {
        var service = new RaffleDescriptionHtml(HostRichTextPolicy.Create());
        var stored = "<p data-secret='x' style='position:fixed;color:red' onclick='evil()'>Saved</p>" +
            "<form action='https://example.com'><input name='password'><button>Pay</button></form>" +
            "<script>evil()</script><style>body{display:none}</style><img src='javascript:evil()' onerror='evil()'>";
        var html = service.Sanitize(stored)!;
        foreach (var forbidden in new[] { "<form", "<input", "<button", "<script", "<style", "data-secret", "onclick", "onerror", "javascript:", "position:" })
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Saved", html);
        Assert.Contains("color", html);
        Assert.Contains("<form", stored); // Read sanitization leaves stored originals untouched.
    }

    [Theory]
    [InlineData("https://www.youtube.com/embed/video", true)]
    [InlineData("https://example.com/embed/video", false)]
    [InlineData("https://www.youtube.com.evil.example/embed/video", false)]
    public void Iframes_retain_host_sandbox_and_origin_policy(string source, bool trusted)
    {
        var html = new RaffleDescriptionHtml(HostRichTextPolicy.Create())
            .Sanitize($"<iframe src='{source}' sandbox='allow-top-navigation' allow='camera'></iframe>")!;
        Assert.Contains("<iframe", html);
        Assert.Contains("sandbox=", html);
        Assert.Contains("allow-scripts allow-forms allow-popups", html);
        Assert.Equal(trusted, html.Contains("allow-same-origin", StringComparison.Ordinal));
        Assert.DoesNotContain("allow-top-navigation", html);
        Assert.DoesNotContain("camera", html);
    }

    [Fact]
    public async Task Create_and_update_share_the_bounded_rich_policy()
    {
        var service = new RaffleService(new InMemoryRaffleDbFactory(),
            new RaffleDescriptionHtml(HostRichTextPolicy.Create()), Microsoft.Extensions.Logging.Abstractions.NullLogger<RaffleService>.Instance);
        var raffle = await service.CreateRaffleAsync("test-store", "Media", "<img src='https://example.com/a.png' onerror='bad()'>", "SATS", 100);
        Assert.Contains("<img", raffle.Description);
        Assert.DoesNotContain("onerror", raffle.Description);
        await service.UpdateDraftRaffleAsync(raffle.Id, "Media", "<video controls src='https://example.com/a.mp4'></video><form>unsafe</form>", "SATS", 100, null);
        var updated = await service.GetRaffleAsync(raffle.Id);
        Assert.Contains("<video", updated!.Description);
        Assert.DoesNotContain("<form", updated.Description);
    }
}
