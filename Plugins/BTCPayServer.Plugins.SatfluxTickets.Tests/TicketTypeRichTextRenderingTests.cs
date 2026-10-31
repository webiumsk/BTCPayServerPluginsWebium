using System.Reflection;
using System.Text.Encodings.Web;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.SatfluxTickets.Controllers;
using Ganss.Xss;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class TicketTypeRichTextRenderingTests
{
    [Fact]
    public async Task Public_ticket_type_partial_preserves_formatting_and_sanitizes_legacy_content()
    {
        var html = await Render("<p data-private='x' onclick='bad()'><strong>VIP</strong></p>" +
            "<img src='https://example.test/a.png' onerror='bad()'><script>bad()</script>" +
            "<iframe src='https://example.test/frame' sandbox='allow-top-navigation'></iframe>");
        Assert.Contains("<strong>VIP</strong>", html);
        Assert.DoesNotContain("&lt;strong", html);
        Assert.Contains("<img", html);
        Assert.Contains("img-fluid", html);
        Assert.Contains("sandbox=", html);
        Assert.DoesNotContain("sales-plain-description", html);
        foreach (var forbidden in new[] { "data-private", "onclick", "onerror", "<script", "allow-top-navigation", "allow-same-origin" })
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_plain_contact_details_and_following_instructions_remain_visible()
    {
        const string description = "Contact <help@example.com> for accessibility details\n  Doors open at 18:00.";
        var html = await Render(description);
        Assert.Contains("Contact &lt;help@example.com&gt; for accessibility details", html);
        Assert.Contains("\n  Doors open at 18:00.", html);
        Assert.Contains("sales-plain-description", html);
        Assert.DoesNotContain("<help@example.com>", html);
    }

    [Fact]
    public async Task Bracketed_contact_text_inside_formatting_is_preserved_without_enabling_unsafe_html()
    {
        var html = await Render("<p><strong>Contact</strong> <help@example.com> for accessibility details</p>" +
            "<img src='javascript:bad()' onerror='bad()'><script>bad()</script>");
        Assert.Contains("<strong>Contact</strong>", html);
        Assert.Contains("&lt;help@example.com&gt; for accessibility details", html);
        Assert.DoesNotContain("sales-plain-description", html);
        foreach (var forbidden in new[] { "javascript:", "onerror", "<script" })
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> Render(string description)
    {
        var factory = typeof(UIInvoiceController).Assembly.GetType("BTCPayServer.Hosting.HtmlSanitizerFactory", true)!;
        var sanitizer = (HtmlSanitizer)factory.GetMethod("CreateRichText", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var safe = new Safe(DispatchProxy.Create<IHtmlHelper, RawHtmlHelper>(), null!, null!, new RichTextHtmlSanitizer(sanitizer));
        var item = typeof(UITicketSalesPublicController).Assembly.GetCustomAttributes<RazorCompiledItemAttribute>()
            .Single(x => x.Identifier == "/Views/UITicketSalesPublic/_TicketTypeDescription.cshtml");
        var page = (RazorPage<string>)Activator.CreateInstance(item.Type, nonPublic: true)!;
        using var writer = new StringWriter();
        page.ViewContext = new ViewContext
        {
            Writer = writer,
            ViewData = new ViewDataDictionary<string>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            {
                Model = description
            }
        };
        page.ViewData = (ViewDataDictionary<string>)page.ViewContext.ViewData;
        page.HtmlEncoder = HtmlEncoder.Default;
        page.GetType().GetProperty("Safe")!.SetValue(page, safe);
        await page.ExecuteAsync();
        return writer.ToString();
    }

    public class RawHtmlHelper : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IHtmlHelper.Raw) ? new HtmlString((string?)args![0]) : throw new NotSupportedException();
    }
}
