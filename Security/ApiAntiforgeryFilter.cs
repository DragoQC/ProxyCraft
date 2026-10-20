using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProxyCraft.Security;

public sealed class ApiAntiforgeryFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        HttpContext http = context.HttpContext;
        string method = http.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
        {
            return;
        }

        if (http.Request.Path.StartsWithSegments("/api") && http.User.Identity is { IsAuthenticated: true, AuthenticationType: ApiKeyAuthenticationHandler.SchemeName })
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestResult();
        }
    }
}
