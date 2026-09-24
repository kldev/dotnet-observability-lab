using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ObservabilityLab.Api;

internal static class ApiTags
{
    public const string Orders = "Sales - Orders";
    public const string Diagnostics = "Lab - Diagnostics";

    /// <summary>In the order Scalar lists them.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> All =
    [
        (
            Orders,
            "Customer orders and their lifecycle: Created → Paid → Completed, or Cancelled. Exists to generate realistic traffic for the observability stack."
        ),
        (
            Diagnostics,
            "Endpoints that deliberately produce slow requests, errors, database failures, CPU load and memory pressure. Available only in Development or with DIAGNOSTICS_ENABLED=true."
        ),
    ];

    /// <summary>Adds tag descriptions for the tags actually used by some operation, in the order of <see cref="All"/>.</summary>
    public static OpenApiOptions AddTagDescriptions(this OpenApiOptions options) =>
        options.AddDocumentTransformer(
            (document, _, _) =>
            {
                var used = document
                    .Paths.Values.SelectMany(p => p.Operations?.Values.AsEnumerable() ?? [])
                    .SelectMany(o => o.Tags?.AsEnumerable() ?? [])
                    .Select(t => t.Name)
                    .ToHashSet();

                document.Tags = new HashSet<OpenApiTag>(
                    All.Where(t => used.Contains(t.Name))
                        .Select(t => new OpenApiTag { Name = t.Name, Description = t.Description })
                );
                return Task.CompletedTask;
            }
        );
}
