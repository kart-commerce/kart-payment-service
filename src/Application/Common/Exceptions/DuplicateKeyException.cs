namespace KartPaymentService.Application.Common.Exceptions;

/// <summary>Translated from a Postgres unique-violation by `EfUnitOfWork` - the database-enforced backstop behind an existence-check race, mapped to 409 via `Kart.Shared.ErrorHandling`.</summary>
public sealed class DuplicateKeyException(string message) : Exception(message);
