using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ObservabilityLab;

public static class Database
{
    public static async Task EnsureSchemaAsync(
        NpgsqlDataSource db,
        ILogger logger,
        CancellationToken ct = default
    )
    {
        // Postgres may still be starting when the app boots (docker compose / local run) - retry a bit.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                await conn.ExecuteAsync(
                    """
                    CREATE TABLE IF NOT EXISTS orders (
                        id            uuid PRIMARY KEY,
                        customer_name text          NOT NULL,
                        total_amount  numeric(12,2) NOT NULL,
                        created_at    timestamptz   NOT NULL,
                        status        text          NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_orders_created_at ON orders (created_at DESC);
                    CREATE INDEX IF NOT EXISTS ix_orders_status_created_at ON orders (status, created_at DESC);
                    """
                );
                logger.LogInformation("Database schema ready");
                return;
            }
            catch (NpgsqlException ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}), retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}

public sealed class PostgresHealthCheck(NpgsqlDataSource db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteScalarAsync<int>("SELECT 1");
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable", ex);
        }
    }
}
