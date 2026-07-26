namespace KartPaymentService.Application.Common.Exceptions;

/// <summary>Translated from `DbUpdateConcurrencyException` - mapped to 412 via `Kart.Shared.ErrorHandling`.</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);
