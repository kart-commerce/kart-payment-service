using System.IdentityModel.Tokens.Jwt;
using KartPaymentService.Application.Common;
using KartPaymentService.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;

namespace KartPaymentService.Infrastructure.Security;

/// <summary>
/// Resolves the acting principal from, in priority order: (1) an ambient
/// <see cref="CurrentPrincipalContext"/> override - set by a non-HTTP caller such as the
/// `OrderCreated` consumer before it calls into a handler that is otherwise shared with an
/// authenticated HTTP path; (2) the caller's Identity-issued access token `sub` claim - a Support
/// Agent's own subject for a manual `POST /payments/{id}/refund` call, or
/// `orderServicePrincipal`'s client-credentials service principal for Order's Saga-compensation
/// refund call; (3) a well-known "unknown" system id as the final fallback.
/// </summary>
public sealed class HttpCurrentPrincipal(IHttpContextAccessor httpContextAccessor) : ICurrentPrincipal
{
    public string ActingPrincipal =>
        CurrentPrincipalContext.Current
        ?? httpContextAccessor.HttpContext?.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? SystemPrincipals.Unknown;
}
