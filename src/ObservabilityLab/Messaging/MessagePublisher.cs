using System.Diagnostics;
using System.Text.Json;
using ObservabilityLab.Telemetry;
using RabbitMQ.Client;

namespace ObservabilityLab.Messaging;

/// <summary>Producer: publishes <see cref="LabMessage"/>s and waits for the broker's confirm. Safe to call from many threads.</summary>
public sealed class MessagePublisher(
    PublisherChannelPool channels,
    TimeProvider time,
    ILogger<MessagePublisher> logger
)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<LabMessage> PublishAsync(
        string text,
        int processingMs,
        bool fail,
        CancellationToken ct
    ) =>
        PublishAsync(
            MessagingTopology.Exchange,
            MessagingTopology.RoutingKey,
            text,
            processingMs,
            fail,
            ct
        );

    public async Task<LabMessage> PublishAsync(
        string exchange,
        string routingKey,
        string text,
        int processingMs,
        bool fail,
        CancellationToken ct
    )
    {
        var message = new LabMessage(
            Guid.CreateVersion7(),
            text,
            processingMs,
            fail,
            time.GetUtcNow()
        );
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.PublishedAt.ToUnixTimeSeconds()),
            AppId = LabTelemetry.ServiceName,
        };

        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            await using var lease = await channels.RentAsync(ct);
            // mandatory: an unroutable message is returned (PublishReturnException) instead of silently dropped.
            await lease.Channel.BasicPublishAsync(
                exchange,
                routingKey,
                mandatory: true,
                properties,
                body,
                ct
            );
            outcome = "confirmed";
        }
        finally
        {
            KeyValuePair<string, object?>[] tags =
            [
                new("outcome", outcome),
                new("exchange", exchange),
                new("routing_key", routingKey),
            ];
            LabTelemetry.MessagesPublished.Add(1, tags);
            LabTelemetry.MessagePublishDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                tags
            );
        }

        logger.LogDebug(
            "Message {MessageId} published to {Exchange} with {RoutingKey}",
            message.Id,
            exchange,
            routingKey
        );
        return message;
    }
}
