using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace KartPaymentService.Api.Security;

/// <summary>
/// api-contract.yaml's `bearerAuth` scheme: an Identity-issued RS256 JWT, checked structurally
/// here (signature + expiry), never re-deriving role grants locally (BRD §24.1 - Identity is the
/// sole issuer of platform role claims). `orderServicePrincipal` (Order's Saga orchestrator) is
/// modeled as the same JWT bearer scheme with a distinguishing `roles` claim value, rather than a
/// second OAuth2 client-credentials scheme - both ultimately resolve to an Identity-issued,
/// JWKS-verified token, so a second authentication *scheme* purely for documentation symmetry with
/// api-contract.yaml's two named security requirements would be ceremony without behavior
/// (coding-standards.md's anti-pattern check).
/// </summary>
public static class AuthenticationExtensions
{
    public const string SupportAgentPolicy = "SupportAgent";
    public const string OrderServicePolicy = "OrderService";
    private const string RolesClaimType = "roles";

    public static IServiceCollection AddPaymentAuthentication(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddHttpClient<JwksSigningKeyResolver>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddScheme<GatewaySignatureAuthenticationOptions, GatewaySignatureAuthenticationHandler>(
                GatewaySignatureAuthenticationHandler.SchemeName, _ => { });

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwksSigningKeyResolver>((options, resolver) =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = resolver.ResolveSigningKeys,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(SupportAgentPolicy, policy => policy.RequireClaim(RolesClaimType, "support_agent"))
            .AddPolicy(OrderServicePolicy, policy => policy.RequireClaim(RolesClaimType, "order-service"));

        return services;
    }
}
