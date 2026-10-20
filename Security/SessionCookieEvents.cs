using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using ProxyCraft.Services;

namespace ProxyCraft.Security;

public sealed class SessionCookieEvents(
    SettingsStore settings,
    Services.AuthenticationService authentication) : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = 401, Title = "Sign in is required." });
        }

        return base.RedirectToLogin(context);
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = 403, Title = "Access is denied." });
        }

        return base.RedirectToAccessDenied(context);
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        string? identifier = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        string? securityStamp = context.Principal?.FindFirstValue("security_stamp");

        if (!settings.Current.AuthenticationEnabled
            || !int.TryParse(identifier, out int userId)
            || securityStamp is null
            || !await authentication.IsSessionValidAsync(userId, securityStamp, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
