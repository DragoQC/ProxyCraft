using Microsoft.AspNetCore.Antiforgery;
using ProxyCraft.Services;

namespace ProxyCraft.Security;

public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ApiKeyService keys, IAntiforgery antiforgery)
    {
        if (context.Request.Path.StartsWithSegments("/api")
            && context.Request.Path != "/api/v1/public/dashboard")
        {
            bool suppliedKey = context.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName);
            bool validKey = context.User.Identity is { IsAuthenticated: true, AuthenticationType: ApiKeyAuthenticationHandler.SchemeName };
            if (suppliedKey && !validKey)
            {
                await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The API key is invalid.").ExecuteAsync(context);
                return;
            }
            Api.Contracts.ApiKeySettingsResponse settings = await keys.GetSettingsAsync(context.RequestAborted);
            if (settings.IsConfigured && !validKey)
            {
                // Browser requests prove their session using the real CSRF token/cookie, never a spoofable UI header.
                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "An X-API-Key header is required for external API requests.").ExecuteAsync(context);
                    return;
                }
            }
        }
        await next(context);
    }
}
