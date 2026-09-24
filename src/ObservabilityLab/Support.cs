using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ObservabilityLab.Orders;

namespace ObservabilityLab;

/// <summary>Stores <see cref="OrderStatus"/> as text in PostgreSQL.</summary>
public sealed class OrderStatusHandler : SqlMapper.TypeHandler<OrderStatus>
{
    public override void SetValue(IDbDataParameter parameter, OrderStatus value) =>
        parameter.Value = value.ToString();

    public override OrderStatus Parse(object value) => Enum.Parse<OrderStatus>((string)value);
}

public static class HealthJson
{
    public static Task Write(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(
                new
                {
                    status = report.Status.ToString(),
                    durationMs = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        durationMs = e.Value.Duration.TotalMilliseconds,
                        error = e.Value.Exception?.Message,
                    }),
                }
            )
        );
    }
}
