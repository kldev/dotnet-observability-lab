using Npgsql;
using ObservabilityLab;
using ObservabilityLab.Diagnostics;
using ObservabilityLab.Endpoints;
using ObservabilityLab.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddDataAccess();
builder.AddApplication();
builder.AddMessaging();
builder.AddHttpApi();
builder.AddLabHealthChecks();
builder.AddLabTelemetry();

var app = builder.Build();

await Database.EnsureSchemaAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<RandomProblemsMiddleware>();

app.MapPrometheusScrapingEndpoint("/metrics");
app.MapLabHealthChecks();
app.MapApiDocs();
app.MapLabEndpoints();

app.Run();

public partial class Program;
