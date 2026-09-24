using ObservabilityLab.Diagnostics;

namespace ObservabilityLab.Endpoints;

/// <summary>The one place that registers every endpoint area.</summary>
public static class MapEndpoints
{
    public static void MapLabEndpoints(this WebApplication app)
    {
        Orders.Endpoint.Map(app);
        Messages.Endpoint.Map(app);

        // Lab-only problem injection: mapped only when enabled, and kept out of the OpenAPI document.
        if (
            app.Environment.IsDevelopment()
            || app.Configuration.GetValue("DIAGNOSTICS_ENABLED", false)
        )
            app.MapProblemEndpoints();
    }
}
