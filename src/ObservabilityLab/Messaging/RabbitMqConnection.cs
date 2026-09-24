using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace ObservabilityLab.Messaging;

/// <summary>
/// The one AMQP connection of the app. <see cref="IConnection"/> is thread-safe and meant to be shared;
/// channels are not - see <see cref="PublisherChannelPool"/>. Opened on first use, so the app starts
/// even when the broker is not up yet; after that the client recovers it on its own.
/// </summary>
public sealed class RabbitMqConnection(
    IConnectionFactory factory,
    ILogger<RabbitMqConnection> logger
) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IConnection? _connection;

    public async ValueTask<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { } connection)
            return connection;

        // Many requests may arrive before the first connect finishes - only one of them opens it.
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { } opened)
                return opened;

            var created = await factory.CreateConnectionAsync(ct);
            await MessagingTopology.DeclareAsync(created, ct);
            logger.LogInformation("Connected to RabbitMQ at {Endpoint}", created.Endpoint);
            return _connection = created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            await connection.CloseAsync();
            connection.Dispose();
        }
        _gate.Dispose();
    }
}

public sealed class RabbitMqHealthCheck(RabbitMqConnection rabbit) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default
    )
    {
        try
        {
            var connection = await rabbit.GetAsync(ct);
            return connection.IsOpen
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("RabbitMQ connection is closed (recovering)");
        }
        catch (BrokerUnreachableException ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ is not reachable", ex);
        }
    }
}
