using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace KartPaymentService.Api.Security;

public sealed class GatewaySignatureAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// api-contract.yaml's `gatewaySignature` scheme for `POST /v1/payments/webhooks/{gateway}` -
/// verifies an HMAC-SHA256 signature over the raw request body against the configured gateway's
/// signing secret (`Gateway:SigningSecrets:{gateway}`, a Kubernetes Secret in production, BRD §22)
/// before any dedup/ordering logic in the handler runs. Never trusts a client-supplied claim -
/// signature verification is the entire authentication decision.
/// </summary>
public sealed class GatewaySignatureAuthenticationHandler(
    IOptionsMonitor<GatewaySignatureAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<GatewaySignatureAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "GatewaySignature";
    private const string SignatureHeaderName = "Gateway-Signature";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SignatureHeaderName, out var signatureHeader) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return AuthenticateResult.Fail("Missing Gateway-Signature header.");
        }

        var gateway = Request.RouteValues["gateway"]?.ToString();
        if (string.IsNullOrEmpty(gateway))
        {
            return AuthenticateResult.Fail("Missing 'gateway' route value.");
        }

        var secret = configuration[$"Gateway:SigningSecrets:{gateway}"];
        if (string.IsNullOrEmpty(secret))
        {
            return AuthenticateResult.Fail($"No signing secret configured for gateway '{gateway}'.");
        }

        Request.EnableBuffering();
        Request.Body.Position = 0;
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
        }

        Request.Body.Position = 0;

        var expectedSignature = ComputeHmacHex(secret, body);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);
        var actualBytes = Encoding.UTF8.GetBytes(signatureHeader.ToString());

        if (expectedBytes.Length != actualBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            return AuthenticateResult.Fail("Gateway-Signature verification failed.");
        }

        var claims = new[] { new Claim(ClaimTypes.Name, $"gateway:{gateway}"), new Claim("roles", "gateway") };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private static string ComputeHmacHex(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
