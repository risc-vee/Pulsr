using System.Collections.Immutable;
using System.Threading.Channels;

namespace Pulstr
{
    /// <summary>
    /// A singleton event broadcaster that allows multiple subscribers to receive events.
    /// Each subscriber gets a dedicated channel for receiving events, and the broadcaster
    /// can broadcast events to all subscribers concurrently.
    /// </summary>
    /// <typeparam name="TEvent">The type of events being broadcasted.</typeparam>
    /// <remarks>
    /// This class is designed to be used as a singleton service in a DI container.
    /// It is thread-safe and can be used in concurrent scenarios.
    /// </remarks>
    public sealed class Pulstr<TEvent> : IDisposable
    {
        // ImmutableList + ImmutableInterlocked ensures thread-safe updates
        // to the list of active subscriber writers. Suitable for a shared singleton.
        private ImmutableList<ChannelWriter<TEvent>> _writers = ImmutableList<ChannelWriter<TEvent>>.Empty;

        // Volatile ensures visibility across threads for the disposed state.
        private volatile bool _disposed = false;

        /// <summary>
        /// Subscribes to events. Creates a dedicated channel for the subscriber.
        /// This method is thread-safe and can be called concurrently by different consumers
        /// of the singleton broadcaster.
        /// </summary>
        /// <returns>
        /// A tuple containing the ChannelReader<TEvent> for the subscriber to consume
        /// and an IDisposable handle to unsubscribe. The subscriber MUST dispose
        /// the handle when finished to prevent resource leaks.
        /// </returns>
        /// <exception cref="ObjectDisposedException">Thrown if the broadcaster singleton instance has been disposed (e.g., during application shutdown).</exception>
        public (ChannelReader<TEvent> Reader, IDisposable Subscription) Subscribe()
        {
            // Check disposed state upfront.
            if (_disposed) throw new ObjectDisposedException(nameof(Pulstr<TEvent>));

            // Create a new channel specifically for this subscriber.
            var channel = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
            {
                SingleReader = true, // We give the reader away for exclusive use
                SingleWriter = false // The singleton broadcaster might write from multiple threads (though unlikely here)
            });

            var writer = channel.Writer;

            // Atomically add the writer to the shared list.
            ImmutableInterlocked.Update(ref _writers, list => list.Add(writer));

            // The subscription handle knows how to remove this specific writer
            // from the shared broadcaster instance.
            var subscription = new Subscription(this, writer);

            // Return the reader end for the subscriber and the handle to unsubscribe
            return (channel.Reader, subscription);
        }

        /// <summary>
        /// Removes a subscriber's channel writer and completes it.
        /// Called internally by Subscription.Dispose(). Thread-safe.
        /// </summary>
        private void Unsubscribe(ChannelWriter<TEvent> writer)
        {
            // Avoid operations if the singleton is globally disposed.
            if (_disposed) return;

            // Atomically remove the writer from the shared list.
            ImmutableInterlocked.Update(ref _writers, list => list.Remove(writer));

            // Signal to the reader associated with this writer that no more items
            // will be coming *from this broadcaster*. Essential for graceful shutdown
            // of the subscriber's reading loop.
            writer.TryComplete();
        }

        /// <summary>
        /// Broadcasts an event to all currently subscribed channel writers.
        /// This method is thread-safe and can be called concurrently.
        /// </summary>
        /// <param name="ev">The event to broadcast.</param>
        public async ValueTask BroadcastAsync(TEvent ev)
        {
            // Check disposed state. If disposed, silently do nothing or throw,
            // depending on desired behavior upon shutdown. Silently is often fine.
            if (_disposed) return;

            // Take an atomic snapshot of the current list of writers.
            // This ensures we work with a consistent set for this specific broadcast.
            var currentWriters = _writers;

            // Optimization: Handle common cases efficiently.
            if (currentWriters.IsEmpty)
            {
                return;
            }
            if (currentWriters.Count == 1)
            {
                // Avoid Task.WhenAll overhead for a single subscriber.
                await WriteToChannelAsync(currentWriters[0], ev).ConfigureAwait(false);
                return;
            }

            // For multiple subscribers, dispatch writes concurrently using Task.WhenAll.
            // This allows faster dispatch if some channel writes take longer, although
            // WriteAsync to Unbounded channels is typically very fast.
            var tasks = new Task[currentWriters.Count];
            for (int i = 0; i < currentWriters.Count; i++)
            {
                tasks[i] = WriteToChannelAsync(currentWriters[i], ev);
            }
            // Await completion of all write attempts for this event.
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Helper to write to a single channel with appropriate error handling,
        /// especially for channels closed by subscribers.
        /// </summary>
        private async Task WriteToChannelAsync(ChannelWriter<TEvent> writer, TEvent ev)
        {
            try
            {
                // Write the event. ConfigureAwait(false) is good practice in libraries/shared services.
                await writer.WriteAsync(ev).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // This is expected if a subscriber unsubscribed (disposed its subscription)
                // between the time we took the snapshot and when we tried to write.
                // Silently ignore is usually the best approach here.
                // Optionally: Could add logic here to attempt to remove the closed writer
                // from the main list if this happens frequently, but that adds complexity.
            }
            catch (Exception ex)
            {
                // Log unexpected errors during the write operation.
                // In a real singleton service, use structured logging (e.g., ILogger).
                Console.WriteLine($"[EventBroadcaster] Error writing event to subscriber channel: {ex.GetType().Name} - {ex.Message}");
                // Continue broadcasting to other subscribers.
            }
        }


        /// <summary>
        /// Disposes the singleton broadcaster instance.
        /// This should typically only be called when the application is shutting down
        /// or the DI container managing the singleton is disposed.
        /// It completes all remaining active subscriber channels.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; // Mark as disposed immediately.

            // Atomically get the final list of writers and clear the shared list
            // to prevent any further Subscribe or Broadcast operations from succeeding fully.
            var writersToCleanup = Interlocked.Exchange(ref _writers, ImmutableList<ChannelWriter<TEvent>>.Empty);

            // Complete all channels that this broadcaster created and managed.
            // This signals all currently active subscribers that the source is gone.
            foreach (var writer in writersToCleanup)
            {
                writer.TryComplete();
            }
            Console.WriteLine($"[EventBroadcaster] Singleton disposed. Completed {writersToCleanup.Count} remaining channels.");
        }

        /// <summary>
        /// Represents the subscription handle returned to the subscriber.
        /// Disposing this handle is crucial for unsubscribing and releasing resources.
        /// </summary>
        private sealed class Subscription : IDisposable
        {
            // Holds a reference back to the singleton broadcaster instance.
            private Pulstr<TEvent>? _broadcaster;
            // Holds the specific writer associated with this subscription.
            private ChannelWriter<TEvent>? _writer;

            public Subscription(Pulstr<TEvent> broadcaster, ChannelWriter<TEvent> writer)
            {
                _broadcaster = broadcaster;
                _writer = writer;
            }

            /// <summary>
            /// Disposes the subscription, signaling the broadcaster to remove
            /// the associated writer and complete the channel.
            /// </summary>
            public void Dispose()
            {
                // Use Interlocked.Exchange to ensure thread-safe, idempotent disposal.
                // This retrieves the writer *once* and sets the field to null.
                var writerToUnsubscribe = Interlocked.Exchange(ref _writer, null);
                if (writerToUnsubscribe != null)
                {
                    // Call Unsubscribe on the potentially shared broadcaster instance.
                    _broadcaster?.Unsubscribe(writerToUnsubscribe);
                    // Release the reference to the broadcaster.
                    _broadcaster = null;
                }
            }
        }
    }
}
