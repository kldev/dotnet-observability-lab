using ObservabilityLab.Messaging;
using ObservabilityLab.Telemetry;
using RabbitMQ.Client;

namespace ObservabilityLab.Hosting;

public static class MessagingExtensions
{
    /// <summary>RabbitMQ: one shared connection, a publisher channel pool and a background consumer.</summary>
    public static IHostApplicationBuilder AddMessaging(this IHostApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<RabbitMqOptions>()
            .Bind(builder.Configuration.GetSection(RabbitMqOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Automatic connection + topology recovery is on by default: after a broker restart the
        // connection, channels, declared queues and the consumer come back without app code.
        builder.Services.AddSingleton<IConnectionFactory>(_ => new ConnectionFactory
        {
            Uri = new Uri(
                builder.Configuration.GetConnectionString("RabbitMq")
                    ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is missing")
            ),
            ClientProvidedName = LabTelemetry.ServiceName,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
        });
        builder.Services.AddSingleton<RabbitMqConnection>();
        builder.Services.AddSingleton<PublisherChannelPool>();
        builder.Services.AddSingleton<MessagePublisher>();
        builder.Services.AddSingleton<ReceivedMessages>();
        builder.Services.AddHostedService<MessageConsumer>();

        return builder;
    }
}
