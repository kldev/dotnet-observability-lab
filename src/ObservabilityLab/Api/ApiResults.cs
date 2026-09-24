namespace ObservabilityLab.Api;

internal static class ApiResults
{
    /// <summary>Error responses shared by the API operations (all returned as ProblemDetails with traceId).</summary>
    public static RouteHandlerBuilder ProducesStandardErrors(this RouteHandlerBuilder builder) =>
        builder
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);
}
