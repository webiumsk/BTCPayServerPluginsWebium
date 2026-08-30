#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.EmailConfirm.Controllers;

/// <summary>
/// Admin Greenfield endpoint that marks a user's email as confirmed.
///
/// Core BTCPay resets <c>EmailConfirmed</c> whenever a user's email changes and
/// exposes no API to set it back - only the server admin UI checkbox. With the
/// "confirmed email required to log in" policy that permanently locks the
/// account's API keys. This endpoint closes the gap for automation.
///
/// Idempotent by design: callers may use it as a capability probe on an
/// already-confirmed user (returns 200 with <c>changed: false</c>).
/// </summary>
[ApiController]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
public class EmailConfirmApiController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;

    public EmailConfirmApiController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [HttpPost("~/api/v1/plugins/email-confirm/users/{idOrEmail}/confirm-email")]
    public async Task<IActionResult> ConfirmEmail(string idOrEmail)
    {
        var user = await _userManager.FindByIdOrEmail(idOrEmail);
        if (user is null)
            return this.UserNotFound();

        if (user.EmailConfirmed)
            return Ok(new EmailConfirmResult { EmailConfirmed = true, Changed = false });

        user.EmailConfirmed = true;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return this.CreateAPIError(500, "email-confirm-failed", "Could not update the user's email confirmation state.");

        return Ok(new EmailConfirmResult { EmailConfirmed = true, Changed = true });
    }
}

public class EmailConfirmResult
{
    public bool EmailConfirmed { get; set; }
    public bool Changed { get; set; }
}
