namespace KartPaymentService.Domain.Payments;

/// <summary>
/// ISO 4217 alphabetic currency code. Protects its own single invariant - exactly three
/// uppercase letters - so <see cref="Money"/> never has to re-validate it. Does not decide
/// which currencies this service actually settles in; that allow-list (if any) is an
/// Application-layer concern, not this type's.
/// </summary>
public readonly record struct CurrencyCode
{
    public string Code { get; }

    public CurrencyCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"'{code}' is not a valid ISO 4217 currency code.", nameof(code));
        }

        Code = code;
    }

    public override string ToString() => Code;
}
