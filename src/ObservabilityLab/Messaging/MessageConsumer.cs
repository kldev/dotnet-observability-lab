using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ObservabilityLab.Telemetry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace ObservabilityLab.Messaging;

/// <summary>
/// Consumer: processes messages from <see cref="MessagingTopology.ConsumedQueues"/> (lab.messages and
/// the q.emails.* queues) on one channel, with up to
/// <see cref="RabbitMqOptions.ConsumerConcurrency"/> handlers in parallel. Acks on success,
/// rejects to the dead-letter queue on failure. Unacked messages go back to the queue when the app stops.
/// </summary>
public sealed class MessageConsumer(
    RabbitMqConnection rabbit,
    ReceivedMessages received,
    IOptions<RabbitMqOptions> options,
    TimeProvider time,
    ILogger<MessageConsumer> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        await using var channel = await OpenChannelAsync(settings, stoppingToken);
        await channel.BasicQosAsync(0, (ushort)settings.Prefetch, global: false, stoppingToken);

        // One consumer per queue, all on this channel: they share its dispatch concurrency,
        // prefetch applies to each of them.
        foreach (var queue in MessagingTopology.ConsumedQueues)
        {
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) =>
                HandleAsync(channel, queue, delivery, stoppingToken);
            await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
        }
        logger.LogInformation(
            "Consuming {Queues} with concurrency {Concurrency}, prefetch {Prefetch}",
            MessagingTopology.ConsumedQueues,
            settings.ConsumerConcurrency,
            settings.Prefetch
        );

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, time, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is stopping - closing the channel hands unacked messages back to the broker.
        }
    }

    private async Task<IChannel> OpenChannelAsync(RabbitMqOptions settings, CancellationToken ct)
    {
        // The broker may still be starting (docker compose, local run) - keep trying; after the first
        // connect the client's automatic recovery restores the channel and this consumer by itself.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var connection = await rabbit.GetAsync(ct);
                // Dispatch concurrency > 1: the client runs ReceivedAsync handlers in parallel on the thread pool.
                // Acks from those handlers on this one channel are fine - the client serializes frame writes;
                // what a channel cannot do safely is concurrent publishing with confirms (see PublisherChannelPool).
                return await connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: false,
                        publisherConfirmationTrackingEnabled: false,
                        consumerDispatchConcurrency: (ushort)settings.ConsumerConcurrency
                    ),
                    ct
                );
            }
            catch (BrokerUnreachableException ex)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 15));
                logger.LogWarning(
                    ex,
                    "RabbitMQ not reachable (attempt {Attempt}), retrying in {Delay}",
                    attempt,
                    delay
                );
                await Task.Delay(delay, time, ct);
            }
        }
    }

    private async Task HandleAsync(
        IChannel channel,
        string queue,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken
    )
    {
        var started = Stopwatch.GetTimestamp();
        LabTelemetry.MessagesInFlight.Add(1);
        LabMessage? message = null;
        MessageOutcome outcome;
        try
        {
            message =
                JsonSerializer.Deserialize<LabMessage>(delivery.Body.Span, MessagePublisher.Json)
                ?? throw new JsonException("Message body is null");
            using var scope = logger.BeginScope(
                new Dictionary<string, object>
                {
                    ["MessageId"] = message.Id,
                    ["Queue"] = queue,
                    ["RoutingKey"] = delivery.RoutingKey,
                }
            );

            if (message.ProcessingMs > 0)
                await Task.Delay(
                    TimeSpan.FromMilliseconds(message.ProcessingMs),
                    time,
                    stoppingToken
                );
            if (message.Fail)
                throw new InvalidOperationException($"Message {message.Id} asked to fail");

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            outcome = MessageOutcome.Acked;
            logger.LogDebug("Message {MessageId} processed", message.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping mid-message: no ack, so the broker redelivers it to the next consumer.
            LabTelemetry.MessagesInFlight.Add(-1);
            return;
        }
        // Consumer boundary: any failure (bad JSON, handler error) rejects the message to the dead-letter
        // queue - requeueing it would redeliver the same poison message in a tight loop.
        catch (Exception ex)
        {
            outcome = MessageOutcome.DeadLettered;
            logger.LogError(
                ex,
                "Message {MessageId} from {Queue} failed, sending it to {DeadLetterQueue}",
                message?.Id.ToString() ?? delivery.BasicProperties.MessageId,
                queue,
                MessagingTopology.DeadLetterQueue
            );
            await channel.BasicNackAsync(
                delivery.DeliveryTag,
                multiple: false,
                requeue: false,
                stoppingToken
            );
        }

        LabTelemetry.MessagesInFlight.Add(-1);
        KeyValuePair<string, object?>[] tags =
        [
            new("outcome", outcome.ToString()),
            new("queue", queue),
        ];
        LabTelemetry.MessagesConsumed.Add(1, tags);
        LabTelemetry.MessageProcessDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            tags
        );

        if (message is null)
            return;
        var completedAt = time.GetUtcNow();
        LabTelemetry.MessageEndToEndDuration.Record(
            (completedAt - message.PublishedAt).TotalSeconds,
            tags
        );
        received.Add(
            new ReceivedMessage(
                message,
                queue,
                delivery.RoutingKey,
                outcome,
                completedAt,
                delivery.Redelivered,
                Environment.CurrentManagedThreadId
            )
        );
    }
}
