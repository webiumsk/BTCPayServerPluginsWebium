using System.Reflection;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Controllers;
using BTCPayServer.TagHelpers;
using Ganss.Xss;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class HostRichTextEditorTests
{
    [Fact]
    public void Existing_native_description_is_sanitized_and_encoded_before_editor_load()
    {
        var factory = typeof(UIInvoiceController).Assembly.GetType("BTCPayServer.Hosting.HtmlSanitizerFactory", true)!;
        var create = factory.GetMethod("CreateRichText", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sanitizer = new RichTextHtmlSanitizer((HtmlSanitizer)create.Invoke(null, null)!);
        var attributes = new TagHelperAttributeList { { "asp-for", "Description" }, { "rich-text", null }, { "class", "form-control richtext" } };
        var context = new TagHelperContext(attributes, new Dictionary<object, object>(), "editor");
        var output = new TagHelperOutput("textarea", attributes,
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        output.Content.SetHtmlContent("&lt;p data-private=&#39;x&#39;&gt;Legacy&lt;/p&gt;&lt;img src=&#39;https://example.com/a.png&#39; onerror=&#39;bad()&#39;&gt;&lt;/textarea&gt;&lt;script&gt;bad()&lt;/script&gt;");
        new RichTextAreaTagHelper(sanitizer).Process(context, output);
        Assert.False(output.Attributes.ContainsName("rich-text"));
        Assert.Contains("rich-text-editor", output.Attributes["class"].Value.ToString());
        var html = output.Content.GetContent();
        Assert.Contains("&lt;img", html);
        Assert.Contains("Legacy", html);
        Assert.DoesNotContain("onerror", html);
        Assert.DoesNotContain("data-private", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("</textarea>", html);
    }
}
