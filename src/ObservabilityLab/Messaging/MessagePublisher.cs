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

    public async Task<LabMessage> PublishAsync(
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
                MessagingTopology.Exchange,
                MessagingTopology.RoutingKey,
                mandatory: true,
                properties,
                body,
                ct
            );
            outcome = "confirmed";
        }
        finally
        {
            LabTelemetry.MessagesPublished.Add(
                1,
                new KeyValuePair<string, object?>("outcome", outcome)
            );
            LabTelemetry.MessagePublishDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("outcome", outcome)
            );
        }

        logger.LogDebug("Message {MessageId} published", message.Id);
        return message;
    }
}
