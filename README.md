## Pulsr

Pulsr is a concurrent, high-performance, in-process publish-subscribe broadcaster based on [.NET Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels). It is designed to simplify message broadcasting by facilitating communication between components - such as in-process services within your web application - without the need for external services like message brokers (e.g., Redis Pub/Sub, RabbitMQ, or Azure Service Bus), although it is not intended as a replacement when those are necessary.

## Why Pulsr?

Pulsr was born out of a practical need: we wanted to send real-time notifications to clients connected via Server-Sent Events (SSE). Each connected client needed to asynchronously receive messages emitted by our background jobs.

However, this wasn't straightforward. Typical solutions required external message brokers like Redis or Kafka to route messages from background tasks to connected clients. We wanted to avoid introducing another moving part to our application stack.

After some research, we discovered .NET Channels, a powerful tool for in-process messaging. However, channels in .NET follow a producer-consumer pattern where only one consumer can read each message, as they are consumed in a competing fashion.

This limitation led us to build Pulsr: a higher-level abstraction over .NET Channels that enables dedicated channels per client, allowing multiple consumers to receive relevant messages independently. Each Pulsr instance centrally manages concurrent subscriptions, unsubscriptions, and broadcasting, providing a clean and efficient way to route messages from producers to many in-process subscribers, all without relying on external infrastructure.
