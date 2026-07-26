using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KartPaymentService.IntegrationTests;

/// <summary>
/// Replaces the real JWT-bearer scheme in tests (no Identity/JWKS to talk to) - always
/// authenticates, deriving `roles` claims from the `X-Test-Roles` header a test sets
/// (comma-separated; defaults to "customer"). The real `GatewaySignatureAuthenticationHandler`
/// is left untouched, since webhook tests compute a genuinely valid HMAC signature.
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rolesHeader = Request.Headers.TryGetValue("X-Test-Roles", out var value) ? value.ToString() : "customer";

        var claims = new List<Claim> { new("sub", "test-user") };
        claims.AddRange(rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(role => new Claim("roles", role.Trim())));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
