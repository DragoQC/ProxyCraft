using Microsoft.AspNetCore.Authorization;
using ProxyCraft.Services;

namespace ProxyCraft.Security;

public sealed class OptionalAuthenticationRequirement : IAuthorizationRequirement;

public sealed class OptionalAuthenticationHandler(SettingsStore settings) : AuthorizationHandler<OptionalAuthenticationRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OptionalAuthenticationRequirement requirement)
    {
        if (!settings.Current.AuthenticationEnabled || context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
