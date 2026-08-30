#nullable enable
using BTCPayServer.Data;
using BTCPayServer.Plugins.EmailConfirm.Controllers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.EmailConfirm.Tests;

public class EmailConfirmApiControllerTests
{
    private static (EmailConfirmApiController Controller, FakeUserStore Store) CreateController(params ApplicationUser[] users)
    {
        var store = new FakeUserStore(users);
        var userManager = new UserManager<ApplicationUser>(
            store, null!, null!, null!, null!, null!, null!, null!, new NullLogger());
        return (new EmailConfirmApiController(userManager), store);
    }

    [Fact]
    public async Task ConfirmsAnUnconfirmedUserAndPersists()
    {
        var user = new ApplicationUser { Id = "u1", Email = "a@b.c", EmailConfirmed = false };
        var (controller, store) = CreateController(user);

        var result = await controller.ConfirmEmail("u1");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<EmailConfirmResult>(ok.Value);
        Assert.True(payload.EmailConfirmed);
        Assert.True(payload.Changed);
        Assert.True(store.Users["u1"].EmailConfirmed);
        Assert.Equal(1, store.UpdateCalls);
    }

    [Fact]
    public async Task IsIdempotentForAnAlreadyConfirmedUser()
    {
        var user = new ApplicationUser { Id = "u1", Email = "a@b.c", EmailConfirmed = true };
        var (controller, store) = CreateController(user);

        var result = await controller.ConfirmEmail("u1");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<EmailConfirmResult>(ok.Value);
        Assert.True(payload.EmailConfirmed);
        Assert.False(payload.Changed);
        // Probe semantics: no write for an already-confirmed user.
        Assert.Equal(0, store.UpdateCalls);
    }

    [Fact]
    public async Task LooksUpByEmailWhenSelectorContainsAt()
    {
        var user = new ApplicationUser { Id = "u1", Email = "a@b.c", NormalizedEmail = "A@B.C", EmailConfirmed = false };
        var (controller, _) = CreateController(user);

        var result = await controller.ConfirmEmail("a@b.c");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ReturnsNotFoundForUnknownUser()
    {
        var (controller, _) = CreateController();

        var result = await controller.ConfirmEmail("missing");

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
    }
}
