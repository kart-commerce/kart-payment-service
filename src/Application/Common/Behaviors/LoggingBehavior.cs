using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Application.Common.Behaviors;

/// <summary>Logs `{RequestName} completed in {ElapsedMilliseconds}ms` for every command/query - deliberately never logs the request/response payload (a charge command carries a gateway token). Mirrors kart-identity-service's `LoggingBehaviour`.</summary>
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await next();
        }
        finally
        {
            logger.LogInformation("{RequestName} completed in {ElapsedMilliseconds}ms", requestName, stopwatch.ElapsedMilliseconds);
        }
    }
}
