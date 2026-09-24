using Microsoft.AspNetCore.Mvc;

namespace ObservabilityLab.Api;

internal static class ApiResults
{
    /// <summary>Errors every API operation can return, all as ProblemDetails (with traceId). No auth in this lab, so no 401/403.</summary>
    public static RouteHandlerBuilder ProducesStandardErrors(this RouteHandlerBuilder builder) =>
        builder
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError);
}
