using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ObservabilityLab.Telemetry;
using RabbitMQ.Client;

namespace ObservabilityLab.Messaging;

/// <summary>
/// Gives each concurrent publisher a channel of its own.
/// <para>
/// <see cref="IChannel"/> must not be used by two threads at once: parallel publishes on one channel
/// mix up publisher confirms (one caller can get another's ack or nack). Opening a channel per publish
/// is safe but costs a round trip to the broker every time. The pool keeps up to
/// <see cref="RabbitMqOptions.PublisherChannels"/> channels and lends each one to a single caller;
/// when all are busy the next caller waits - that wait is the lab.rabbitmq.publisher.channel.wait metric.
/// </para>
/// </summary>
public sealed class PublisherChannelPool : IAsyncDisposable
{
    // Confirmations + tracking: BasicPublishAsync completes only when the broker confirms the message,
    // and throws PublishException when it nacks or returns it (unroutable with mandatory: true).
    private static readonly CreateChannelOptions ChannelOptions = new(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true
    );

    private readonly RabbitMqConnection _connection;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentQueue<IChannel> _idle = new();
    private volatile bool _disposed;

    public PublisherChannelPool(RabbitMqConnection connection, IOptions<RabbitMqOptions> options)
    {
        _connection = connection;
        var size = options.Value.PublisherChannels;
        _slots = new SemaphoreSlim(size, size);

        LabTelemetry.Meter.CreateObservableGauge(
            "lab.rabbitmq.publisher.channels.max",
            () => size,
            "{channel}",
            "Publisher channel pool size"
        );
    }

    /// <summary>Waits for a free channel. Dispose the lease to hand the channel back.</summary>
    public async ValueTask<Lease> RentAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var waitStarted = Stopwatch.GetTimestamp();
        await _slots.WaitAsync(ct);
        LabTelemetry.PublisherChannelWait.Record(
            Stopwatch.GetElapsedTime(waitStarted).TotalSeconds
        );

        try
        {
            var channel = await TakeIdleAsync() ?? await OpenAsync(ct);
            LabTelemetry.PublisherChannelsInUse.Add(1);
            return new Lease(this, channel);
        }
        catch
        {
            _slots.Release(); // no channel handed out - free the slot for the next caller
            throw;
        }
    }

    private async ValueTask<IChannel?> TakeIdleAsync()
    {
        while (_idle.TryDequeue(out var channel))
        {
            if (channel.IsOpen)
                return channel;
            await channel.DisposeAsync(); // closed by the broker (e.g. after a channel error)
        }
        return null;
    }

    private async Task<IChannel> OpenAsync(CancellationToken ct)
    {
        var connection = await _connection.GetAsync(ct);
        var channel = await connection.CreateChannelAsync(ChannelOptions, ct);
        LabTelemetry.PublisherChannelsOpened.Add(1);
        return channel;
    }

    private async ValueTask ReturnAsync(IChannel channel)
    {
        // A channel closed during use (channel-level error) is not reused.
        if (channel.IsOpen && !_disposed)
            _idle.Enqueue(channel);
        else
            await channel.DisposeAsync();

        LabTelemetry.PublisherChannelsInUse.Add(-1);
        _slots.Release();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idle.TryDequeue(out var channel))
            await channel.DisposeAsync();
    }

    /// <summary>Exclusive use of one channel until disposed.</summary>
    public sealed class Lease(PublisherChannelPool pool, IChannel channel) : IAsyncDisposable
    {
        private int _returned;

        public IChannel Channel => channel;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _returned, 1) == 0
                ? pool.ReturnAsync(channel)
                : ValueTask.CompletedTask;
    }
}
