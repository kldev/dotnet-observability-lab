using System.Text.Json.Serialization;
using ObservabilityLab.Api;
using Scalar.AspNetCore;

namespace ObservabilityLab.Hosting;

public static class HttpApiExtensions
{
    /// <summary>ProblemDetails (with traceId), built-in validation, JSON enums as strings, OpenAPI document.</summary>
    public static IHostApplicationBuilder AddHttpApi(this IHostApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();

        // Bad input that fails binding (e.g. unknown enum value) is a 400, not a 500 - also in Development,
        // where minimal APIs throw BadHttpRequestException instead of answering 400 directly.
        builder.Services.Configure<ExceptionHandlerOptions>(o =>
            o.StatusCodeSelector = ex =>
                ex is BadHttpRequestException bad
                    ? bad.StatusCode
                    : StatusCodes.Status500InternalServerError
        );
        builder.Services.AddValidation();
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(allowIntegerValues: false)
            )
        );
        builder.Services.AddOpenApi(
            "v1",
            o =>
            {
                o.AddDocumentTransformer(
                    (document, _, _) =>
                    {
                        document.Info.Title = "Observability Lab";
                        document.Info.Version = "v1";
                        document.Info.Description = """
                            A deliberately small Orders API that exists to generate logs, metrics and traces
                            for the observability stack (Prometheus, Grafana, Rootprint on RustFS/S3).
                            Problem-injection endpoints (/diagnostics/*) are lab tools and are not part of this document.
                            """;
                        return Task.CompletedTask;
                    }
                );
                o.AddTagDescriptions();
            }
        );

        return builder;
    }

    /// <summary>/openapi/v1.json, Scalar on /docs and / redirecting there.</summary>
    public static WebApplication MapApiDocs(this WebApplication app)
    {
        app.MapOpenApi();
        app.MapScalarApiReference("/docs", o => o.Title = "Observability Lab API");
        app.MapGet("/", () => TypedResults.Redirect("/docs")).ExcludeFromDescription();
        return app;
    }
}
