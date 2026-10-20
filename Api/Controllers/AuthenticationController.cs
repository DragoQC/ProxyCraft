using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Models;
using ProxyCraft.Services;

namespace ProxyCraft.Api.Controllers;

[ApiController, Route("api/v1/authentication"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthenticationController(
    SettingsStore settings,
    Services.AuthenticationService authentication,
    TwoFactorService factors) : ControllerBase
{
    [AllowAnonymous, HttpGet("session")]
    [EndpointSummary("Read the current session")]
    [EndpointDescription("Returns auth mode, sign-in state, and a browser CSRF token. Available without sign-in, but a configured API key still requires the key or a dashboard token/cookie pair. Refresh the token after signing in.")]
    public SessionResponse Session(IAntiforgery antiforgery)
    {
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return new SessionResponse(settings.Current.AuthenticationEnabled, User.Identity?.IsAuthenticated == true,
            User.Identity?.Name, tokens.RequestToken ?? throw new InvalidOperationException("No antiforgery token was generated."));
    }

    [HttpPost("enable")]
    [EndpointSummary("Enable dashboard sign-in")]
    [EndpointDescription("Sets the main account using one username OR email and a password. Signs out the browser; the user must sign in with the new credentials. Returns 409 if sign-in is already enabled.")]
    [ProducesResponseType<AppSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppSettingsResponse>> Enable(CredentialsRequest request, CancellationToken cancellationToken)
    {
        if (!await authentication.EnableAsync(request, cancellationToken))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Authentication is already enabled.");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return new AppSettingsResponse(true, settings.Current.PublicDashboardEnabled, settings.Current.ShowPublicDomains);
    }

    [HttpPost("disable")]
    [EndpointSummary("Disable dashboard sign-in")]
    [EndpointDescription("Disables sign-in and signs out the browser. Requires an authenticated session or valid API key while sign-in is enabled.")]
    public async Task<AppSettingsResponse> Disable(CancellationToken cancellationToken)
    {
        await authentication.DisableAsync(cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return new AppSettingsResponse(false, settings.Current.PublicDashboardEnabled, settings.Current.ShowPublicDomains);
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("login")]
    [EndpointSummary("Sign in to the dashboard")]
    [EndpointDescription("Accepts one username OR email and a password. When 2FA is enabled, twoFactorCode must be a fresh six-digit authenticator code or unused recovery code. No session cookie is issued until both factors pass. Limited to 10 attempts per minute per IP. A configured API key still applies to this endpoint.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        User? user = await authentication.AuthenticateAsync(request, cancellationToken);

        if (user is null || !await factors.VerifyAsync(user.Id, request.TwoFactorCode, cancellationToken))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The username/email, password, or two-factor code is incorrect.");
        }

        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
             new Claim(ClaimTypes.Name, user.UsernameOrEmail),
             new Claim("security_stamp", user.SecurityStamp)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return NoContent();
    }

    [HttpPost("logout")]
    [EndpointSummary("Sign out of the dashboard")]
    [EndpointDescription("Clears the caller's browser session cookie.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
