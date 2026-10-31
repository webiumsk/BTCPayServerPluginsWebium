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

    [Theory]
    [InlineData("RedirectUrl")]
    [InlineData("vm.RedirectUrl")]
    public void Unchanged_legacy_native_redirect_clears_only_its_validation_error(string key)
    {
        var controller = NativeController();
        var current = new BTCPayServer.Plugins.SatfluxTickets.Data.Event
        {
            RedirectUrl = "/legacy", EventType = BTCPayServer.Plugins.SatfluxTickets.Data.EventType.Physical,
            TimeZone = "UTC", StartDate = new DateTime(2035, 11, 28, 8, 0, 0, DateTimeKind.Utc)
        };
        var model = new UpdateSimpleTicketSalesEventViewModel
        {
            RedirectUrl = current.RedirectUrl, EventType = current.EventType, TimeZone = "UTC",
            StartLocal = EventTimeZoneService.Civil(current.StartDate, "UTC")
        };
        controller.ModelState.AddModelError(key, TicketRedirectUrlAttribute.ValidationMessage);
        Assert.True(Apply(controller, model, current));
        Assert.True(controller.ModelState.IsValid);
        Assert.Equal("/legacy", model.RedirectUrl);
        Assert.False(TicketRedirectUrlAttribute.IsAllowed(model.RedirectUrl)); // Still unsuitable for new purchases.

        controller.ModelState.AddModelError(key, TicketRedirectUrlAttribute.ValidationMessage);
        controller.ModelState.AddModelError(key, "Other binding error");
        Assert.False(Apply(controller, model, current));
        Assert.Equal("Other binding error", Assert.Single(controller.ModelState[key]!.Errors).ErrorMessage);
        Assert.False(controller.ModelState.IsValid);
    }

    [Theory]
    [InlineData("/changed")]
    [InlineData("javascript:alert(1)")]
    public void Native_edit_cannot_replace_a_legacy_redirect_with_another_invalid_destination(string destination)
    {
        var controller = NativeController();
        var model = new UpdateSimpleTicketSalesEventViewModel { RedirectUrl = destination };
        var current = new BTCPayServer.Plugins.SatfluxTickets.Data.Event { RedirectUrl = "/legacy" };
        Assert.False(Apply(controller, model, current));
        Assert.NotEmpty(controller.ModelState[nameof(model.RedirectUrl)]!.Errors);
    }

    private static UITicketSalesController NativeController() =>
        new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static bool Apply(UITicketSalesController controller, UpdateSimpleTicketSalesEventViewModel model,
        BTCPayServer.Plugins.SatfluxTickets.Data.Event current) =>
        (bool)typeof(UITicketSalesController).GetMethod("ApplyNativeTimes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, new object[] { model, current })!;

    [Fact]
    public void Url_length_is_bounded_with_no_truncation()
    {
        var prefix = "https://example.com/";
        var max = prefix + new string('a', 2048 - prefix.Length);
        Assert.True(TicketRedirectUrlAttribute.IsAllowed(max));
        Assert.False(TicketRedirectUrlAttribute.IsAllowed(max + "a"));
    }
}
