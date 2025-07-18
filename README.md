## Pulsr

Pulsr is a concurrent and high-performance, in-process pub-sub broadcaster based on [.NET Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels). It is designed to simplify message broadcasting by facilitating communication between components - such as in-process services within your web application - without the need for external services like message brokers (e.g., Redis Pub/Sub, RabbitMQ, or Azure Service Bus), although it is not intended as a replacement when those are necessary.

## Why Pulsr?

Pulsr was born out of a practical need: we wanted to send real-time notifications to clients connected via Server-Sent Events (SSE). Each connected client needed to asynchronously receive messages emitted by our background jobs.

However, this wasn't straightforward. Typical solutions often involve using external message brokers like Redis Pub/Sub or RabbitMQ to route messages from background tasks to connected clients. We wanted to avoid introducing another moving part to our application stack.

After some research, we discovered .NET Channels, a powerful tool for in-process messaging. However, channels in .NET follow a producer-consumer pattern where only one consumer can read each message, as they are consumed in a competing fashion.

This limitation led us to build Pulsr: a higher-level abstraction over .NET Channels that enables dedicated channels per client, allowing multiple consumers to receive relevant messages independently. Each Pulsr instance centrally manages concurrent subscriptions, unsubscriptions, and broadcasting, providing a clean and efficient way to route messages from producers to many in-process subscribers, all without relying on external infrastructure.


## Usage

1. **Create a message payload to be sent:**

    ```csharp
    public record Event(int Value);
    ```

2. **Register a Pulsr singleton instance in the DI container:**

    ```csharp
    builder.Services.AddPulsr<Event>();
    ```

3. **Inject Pulsr into a publisher service and broadcast messages:**

    ```csharp
    public class PublisherBackgroundJob(
        ILogger<PublisherBackgroundJob> logger,
        Pulsr<Event> eventBroadcaster) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var jobTimer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            try
            {
                do
                {
                    await eventBroadcaster.BroadcastAsync(new Event(123));
                }
                while (await jobTimer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation($"[{DateTimeOffset.UtcNow}] {nameof(PublisherBackgroundJob)} cancelled");
            }
        }
    }
    ```

4. **Subscribe to events from the registered Pulsr instance:**

    ```csharp
    public class SubscriberBackgroundJob(
        ILogger<SubscriberBackgroundJob> logger,
        Pulsr<Event> eventBroadcaster) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var (reader, subscription) = eventBroadcaster.Subscribe();
                using (subscription)
                {
                    while (await reader.WaitToReadAsync(stoppingToken))
                    {
                        if (reader.TryRead(out var @event))
                        {
                            logger.LogInformation($"New event received: {@event}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation($"[{DateTimeOffset.UtcNow}] {nameof(SubscriberBackgroundJob)} cancelled");
            }
        }
    }
    ```
