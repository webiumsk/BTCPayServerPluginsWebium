using System.Text;
using BTCPayServer.Plugins.CashuMelt.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

public class CashuMeltRecoveryApiTests
{
    [Fact]
    public void Recovery_requires_json_so_cookie_authenticated_simple_form_posts_are_not_accepted()
    {
        var method = typeof(CashuMeltApiController).GetMethod(nameof(CashuMeltApiController.ConfirmOriginalSettings))!;
        var attribute = Assert.Single(method.GetCustomAttributes(typeof(ConsumesAttribute), false));
        Assert.Contains("application/json", Assert.IsType<ConsumesAttribute>(attribute).ContentTypes);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"confirmedOriginal\":false,\"mintUrl\":\"https://mint.example\",\"lightningAddress\":\"m@example.com\"}")]
    [InlineData("{\"confirmedOriginal\":\"true\",\"mintUrl\":\"https://mint.example\",\"lightningAddress\":\"m@example.com\"}")]
    [InlineData("{\"confirmedOriginal\":true,\"mintUrl\":null,\"lightningAddress\":\"m@example.com\"}")]
    [InlineData("{\"confirmedOriginal\":true,\"mintUrl\":\"https://mint.example\",\"lightningAddress\":\"  \"}")]
    public async Task Invalid_or_unconfirmed_recovery_never_accesses_payment_services(string json)
    {
        // Deliberately absent dependencies prove validation occurs before DB/network access.
        var controller = new CashuMeltApiController(null!, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.IsType<BadRequestObjectResult>(await controller.ConfirmOriginalSettings("store", "quote"));
    }
}
