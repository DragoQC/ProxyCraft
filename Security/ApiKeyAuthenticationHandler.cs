using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ProxyCraft.Services;

namespace ProxyCraft.Security;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-API-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out StringValues values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 || !await keys.ValidateAsync(values[0] ?? string.Empty, Context.RequestAborted))
        {
            return AuthenticateResult.Fail("The API key is invalid.");
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!)));
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.NameIdentifier, "api-key"), new Claim("api_key_hash", hash)],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
