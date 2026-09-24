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
/// the q.emails.* queues). Every queue has a channel of its own with up to
/// <see cref="RabbitMqOptions.ConsumerConcurrency"/> handlers in parallel, so a slow queue cannot take
/// the handler slots of the others. Acks on success, rejects to the dead-letter queue on failure.
/// Unacked messages go back to the queue when the app stops.
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
        var connection = await ConnectAsync(stoppingToken);

        // Channels are opened concurrently - each is a round trip to the broker.
        var consuming = MessagingTopology
            .ConsumedQueues.Select(queue =>
                ConsumeAsync(connection, queue, settings, stoppingToken)
            )
            .ToArray();
        try
        {
            await Task.WhenAll(consuming);
            logger.LogInformation(
                "Consuming {Queues}, one channel each: concurrency {Concurrency}, prefetch {Prefetch}",
                MessagingTopology.ConsumedQueues,
                settings.ConsumerConcurrency,
                settings.Prefetch
            );
            await Task.Delay(Timeout.InfiniteTimeSpan, time, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App is stopping - closing the channels hands unacked messages back to the broker.
        }
        finally
        {
            foreach (var channel in consuming.Where(c => c.IsCompletedSuccessfully))
                await (await channel).DisposeAsync();
        }
    }

    private async Task<IConnection> ConnectAsync(CancellationToken ct)
    {
        // The broker may still be starting (docker compose, local run) - keep trying; after the first
        // connect the client's automatic recovery restores the channels and consumers by itself.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await rabbit.GetAsync(ct);
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

    private async Task<IChannel> ConsumeAsync(
        IConnection connection,
        string queue,
        RabbitMqOptions settings,
        CancellationToken stoppingToken
    )
    {
        // Dispatch concurrency > 1: the client runs ReceivedAsync handlers of this channel in parallel on
        // the thread pool. Acks from those handlers on the one channel are fine - the client serializes
        // frame writes; what a channel cannot do safely is concurrent publishing with confirms (see PublisherChannelPool).
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: (ushort)settings.ConsumerConcurrency
            ),
            stoppingToken
        );
        try
        {
            await channel.BasicQosAsync(0, (ushort)settings.Prefetch, global: false, stoppingToken);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) =>
                HandleAsync(channel, queue, delivery, stoppingToken);
            await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync(); // not handed to ExecuteAsync - close it here
            throw;
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
