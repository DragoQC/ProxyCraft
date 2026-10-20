using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Services;

namespace ProxyCraft.Api.Controllers;

[ApiController, Route("api/v1/authentication/two-factor")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TwoFactorController(TwoFactorService factors) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    [HttpGet]
    [EndpointSummary("Read your two-factor status")]
    [EndpointDescription("Requires a signed-in dashboard account. API keys cannot enroll or remove a user's factor.")]
    public Task<TwoFactorSettingsResponse> Get(CancellationToken cancellationToken) => factors.GetAsync(UserId, cancellationToken);

    [HttpPost("setup"), EnableRateLimiting("login")]
    [EndpointSummary("Start authenticator setup")]
    [EndpointDescription("Requires your password. Returns a manual authenticator secret, otpauth URI, and protected setup token valid for ten minutes. Does not enable 2FA until confirmed.")]
    [ProducesResponseType<TwoFactorSetupResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TwoFactorSetupResponse>> Setup(TwoFactorPasswordRequest request, CancellationToken cancellationToken)
    {
        TwoFactorSetupResponse? response = await factors.BeginAsync(UserId, request.Password, cancellationToken);
        return response is null ? Problem(statusCode: 400, title: "Check your password and current two-factor status.") : Ok(response);
    }

    [HttpPost("confirm"), EnableRateLimiting("login")]
    [EndpointSummary("Confirm and enable two-factor authentication")]
    [EndpointDescription("Verifies a six-digit authenticator code, returns eight one-use recovery codes once, and signs out existing sessions. Save the recovery codes before signing in again.")]
    [ProducesResponseType<TwoFactorEnabledResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TwoFactorEnabledResponse>> Confirm(ConfirmTwoFactorRequest request, CancellationToken cancellationToken)
    {
        TwoFactorEnabledResponse? response = await factors.ConfirmAsync(UserId, request, cancellationToken);
        if (response is null)
        {
            return Problem(statusCode: 400, title: "The setup token or authenticator code is invalid or expired.");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(response);
    }

    [HttpPost("disable"), EnableRateLimiting("login")]
    [EndpointSummary("Disable two-factor authentication")]
    [EndpointDescription("Requires your password and a fresh authenticator code or unused recovery code, then signs out existing sessions.")]
    public async Task<IActionResult> Disable(DisableTwoFactorRequest request, CancellationToken cancellationToken)
    {
        if (!await factors.DisableAsync(UserId, request, cancellationToken))
        {
            return Problem(statusCode: 400, title: "The password or two-factor code is incorrect.");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
