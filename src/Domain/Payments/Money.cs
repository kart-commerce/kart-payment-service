namespace KartPaymentService.Domain.Payments;

/// <summary>
/// An amount of money denominated in a specific currency. Protects two invariants: the amount
/// is never negative, and every operation is currency-safe - adding/comparing two <see cref="Money"/>
/// values in different currencies throws rather than silently producing a nonsense number. Does
/// NOT decide what counts as a valid business amount (minimum charge, refund ceiling, Support
/// Agent cap) - those are entity-level rules that stay on <see cref="PaymentIntent"/>.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }

    public CurrencyCode Currency { get; }

    public Money(decimal amount, CurrencyCode currency)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Money amount cannot be negative.");
        }

        Amount = amount;
        Currency = currency;
    }

    public static Money Zero(CurrencyCode currency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public bool IsGreaterThan(Money other)
    {
        EnsureSameCurrency(other);
        return Amount > other.Amount;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot operate on Money values in different currencies ({Currency} vs {other.Currency}).");
        }
    }

    public override string ToString() => $"{Amount} {Currency}";
}
