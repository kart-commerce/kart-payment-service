using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace KartPaymentService.Application.Common.Behaviors;

/// <summary>Logs `{RequestName} completed in {ElapsedMilliseconds}ms` for every command/query - deliberately never logs the request/response payload (a charge command carries a gateway token).</summary>
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        var response = await next();

        logger.LogInformation(
            "{RequestName} completed in {ElapsedMilliseconds}ms",
            requestName,
            stopwatch.ElapsedMilliseconds);

        return response;
    }
}
