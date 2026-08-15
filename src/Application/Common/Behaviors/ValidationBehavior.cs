using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Application.Common.Behaviors;

/// <summary>Runs every registered `AbstractValidator&lt;TRequest&gt;` before the handler; throws FluentValidation's own `ValidationException`, which `Kart.Shared.ErrorHandling.KartExceptionHandler` special-cases to `400` with a per-field error map. Mirrors kart-identity-service's `ValidationBehaviour`.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators,
    ILogger<ValidationBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = (await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, cancellationToken))))
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToList();

        if (failures.Count > 0)
        {
            var requestName = typeof(TRequest).Name;

            // Checkpoint-logging taxonomy stage 4 ("<Rule>ValidationFailed", logged at Warning
            // with the reason before throwing) generalized here for every FluentValidation
            // validator platform-wide, rather than duplicated per handler - the ValidationException
            // itself is still logged once more, generically, at the API boundary by
            // Kart.Shared.ErrorHandling.KartExceptionHandler; this line is the one that's
            // greppable by Stage and carries the actual field-level reasons. Never logs the
            // request object itself (a charge/refund command carries a gateway token/amount that,
            // while not raw card data, still shouldn't be echoed wholesale into logs).
            logger.LogWarning(
                "Stage {Stage}: {RequestName} rejected — {Errors}",
                $"{requestName}ValidationFailed",
                requestName,
                string.Join("; ", failures.Select(f => $"{f.PropertyName}: {f.ErrorMessage}")));

            throw new ValidationException(failures);
        }

        return await next();
    }
}
