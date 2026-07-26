using Kart.Shared.Domain;
using Kart.Shared.ErrorHandling;
using Microsoft.AspNetCore.Mvc;

namespace KartPaymentService.Api.Common;

/// <summary>
/// Translates a Handler's `Result`/`Result&lt;T&gt;` failure (api-standards.md: "Domain/business
/// errors use a Result/Either pattern - not exceptions") into the same `ProblemDetails` envelope
/// `Kart.Shared.ErrorHandling`'s exception-mapping path produces for thrown exceptions - one
/// consistent error shape platform-wide, regardless of which path in a handler produced the
/// rejection (design-decisions.md's Global Exception Handling & Consistent Response Model).
/// </summary>
public static class ResultExtensions
{
    public static ActionResult<TResponse> ToActionResult<TValue, TResponse>(
        this ControllerBase controller,
        Result<TValue> result,
        Func<TValue, ActionResult<TResponse>> onSuccess)
    {
        return result.IsSuccess ? onSuccess(result.Value) : new ActionResult<TResponse>(controller.MapFailure(result.Error));
    }

    public static ActionResult MapFailure(this ControllerBase controller, Error error)
    {
        var statusCode = error.Code switch
        {
            "validation_error" => StatusCodes.Status400BadRequest,
            "unauthorized" => StatusCodes.Status401Unauthorized,
            "not_found" => StatusCodes.Status404NotFound,
            "conflict" or "refund_cap_exceeded" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

        var problem = KartProblemDetailsFactory.Create(controller.HttpContext, statusCode, error.Code, error.Message);
        return controller.StatusCode(statusCode, problem);
    }
}
