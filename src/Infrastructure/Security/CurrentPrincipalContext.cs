namespace KartPaymentService.Infrastructure.Security;

/// <summary>
/// Lets a non-HTTP caller (the `OrderCreated` consumer) stamp the correct `system:*` audit actor
/// (BRD §24.3) on a call into <see cref="Application.Features.ChargePayment.ChargePaymentCommandHandler"/>
/// - the same handler an authenticated HTTP request also calls, via the same
/// <see cref="Application.Common.Interfaces.ICurrentPrincipal"/> abstraction. `AsyncLocal` scopes
/// the override to the async call chain a single message dispatch spans, never leaking across
/// concurrently-processed messages.
/// </summary>
public static class CurrentPrincipalContext
{
    private static readonly AsyncLocal<string?> Ambient = new();

    public static string? Current => Ambient.Value;

    public static IDisposable SetScope(string principal)
    {
        Ambient.Value = principal;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }
}
