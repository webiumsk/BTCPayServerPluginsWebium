using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using BTCPayServer.Plugins.SatfluxTickets.Controllers;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.SatfluxTickets.Models.Api;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Plugins.SatfluxTickets.ViewModels;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class TicketRedirectUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/received?order=123&key=test#receipt")]
    [InlineData("http://example.com/return")]
    [InlineData("HTTPS://example.com/return")]
    public void Optional_or_absolute_web_redirects_are_preserved(string? url)
    {
        Assert.True(TicketRedirectUrlAttribute.IsAllowed(url!));
        var request = new ApiEventRequest { RedirectUrl = url! };
        Assert.Equal(url, request.RedirectUrl);
    }

    [Theory]
    [InlineData("/received")]
    [InlineData("//example.com/received")]
    [InlineData("javascript:alert(1)")]
    [InlineData("lightning:lnurl")]
    [InlineData("file:///tmp/return")]
    [InlineData("ftp://example.com/return")]
    [InlineData(" ")]
    [InlineData(" https://example.com/return")]
    [InlineData("https://example.com/return\r\nX-Test: bad")]
    public void Unsafe_or_nonabsolute_values_fail_on_all_authoring_models(string url)
    {
        Assert.False(TicketRedirectUrlAttribute.IsAllowed(url));
        foreach (var model in new object[] { new ApiEventRequest { RedirectUrl = url }, new CreatePurchaseRequest { RedirectUrl = url }, new UpdateSimpleTicketSalesEventViewModel { RedirectUrl = url } })
        {
            var property = model.GetType().GetProperty("RedirectUrl")!;
            var attribute = Assert.IsType<TicketRedirectUrlAttribute>(Attribute.GetCustomAttribute(property, typeof(TicketRedirectUrlAttribute)));
            Assert.NotEqual(ValidationResult.Success, attribute.GetValidationResult(url, new ValidationContext(model)));
        }
    }

    [Fact]
    public void Native_form_rejects_invalid_redirect_before_interpreting_or_saving_the_draft()
    {
        var controller = new UITicketSalesController(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var apply = typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var model = new UpdateSimpleTicketSalesEventViewModel { RedirectUrl = "/relative", Title = "Draft" };
        Assert.False((bool)apply.Invoke(controller, new object[] { model, null! })!);
        Assert.NotEmpty(controller.ModelState[nameof(model.RedirectUrl)]!.Errors);
        Assert.Equal("Draft", model.Title);
        Assert.Equal("/relative", model.RedirectUrl);
    }

    [Fact]
    public void Url_length_is_bounded_with_no_truncation()
    {
        var prefix = "https://example.com/";
        var max = prefix + new string('a', 2048 - prefix.Length);
        Assert.True(TicketRedirectUrlAttribute.IsAllowed(max));
        Assert.False(TicketRedirectUrlAttribute.IsAllowed(max + "a"));
    }
}
