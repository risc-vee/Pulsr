using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Pulsr
{
    /// <summary>
    /// An in-process publish-subscribe broadcaster that concurrently distributes messages
    /// to multiple subscribers. Each subscriber receives messages through a dedicated channel,
    /// ensuring isolated and efficient message delivery.
    /// </summary>
    /// <typeparam name="TMessage">The type of messages to broadcast.</typeparam>
    /// <remarks>
    /// This class is designed to be used as a singleton service in a DI container,
    /// but it can also be instantiated manually if desired.
    /// It is thread-safe and can be used in concurrent scenarios.
    /// </remarks>
    public sealed class Pulsr<TMessage> : IDisposable
    {
        private static long _nextSubscriptionId = 0;

        private ConcurrentDictionary<long, ChannelWriter<TMessage>> _writers = new();

        private volatile bool _disposed = false;

        /// <summary>
        /// Subscribes to messages by creating a dedicated channel for the subscriber.
        /// This method is thread-safe and can be called concurrently by multiple subscribers.
        /// </summary>
        /// <returns>
        /// A tuple containing a <see cref="ChannelReader{TMessage}"/> for receiving messages,
        /// and an <see cref="IDisposable"/> handle for unsubscribing. The subscriber must dispose
        /// the handle when finished to prevent resource leaks.
        /// </returns>
        /// <exception cref="ObjectDisposedException">
        /// Thrown if <see cref="Pulsr"/> has been disposed
        /// </exception>
        public (ChannelReader<TMessage> Reader, IDisposable Subscription) Subscribe()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Pulsr<TMessage>));

            var channel = Channel.CreateUnbounded<TMessage>(new UnboundedChannelOptions
            {
                SingleReader = true, //we tell the channel there's no need to synchronize access to the reader
                SingleWriter = false //the channel can be written to concurrently
            });

            var subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);
            
            _writers.TryAdd(subscriptionId, channel.Writer);
            
            //The subscription handle knows how to remove this specific writer
            //from the shared broadcaster instance.
            var subscription = new Subscription(this, subscriptionId);

            return (channel.Reader, subscription);
        }

        /// <summary>
        /// Removes a subscriber's channel by its unique ID and completes it.
        /// </summary>
        private void Unsubscribe(long subscriptionId)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Pulsr<TMessage>));

            if (_writers.TryRemove(subscriptionId, out var writer))
            {
                writer.TryComplete();
            }
        }

        /// <summary>
        /// Broadcasts a message to all currently subscribed channel writers.
        /// This method is thread-safe and can be called concurrently.
        /// </summary>
        public async ValueTask BroadcastAsync(TMessage message)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Pulsr<TMessage>));

            if (_writers.IsEmpty)
            {
                return;
            }

            if (_writers.Count == 1)
            {
                var (subscriptionId, writer) = _writers.Single();
                await WriteToChannelAsync(subscriptionId, writer, message).ConfigureAwait(false);
                return;
            }

            var tasks = new List<Task>(_writers.Count);
            foreach (var (subscriptionId, writer) in _writers)
            {
                tasks.Add(WriteToChannelAsync(subscriptionId, writer, message));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task WriteToChannelAsync(long subscriptionId, ChannelWriter<TMessage> writer, TMessage ev)
        {
            try
            {
                await writer.WriteAsync(ev).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            { }
            catch (Exception ex)
            {
                //TODO: Replace with injected ILogger for proper production logging.
                Console.WriteLine($"[Pulsr] Error writing to subscriber {subscriptionId}: {ex.Message}");
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
            if (_disposed) throw new ObjectDisposedException(nameof(Pulsr<TMessage>));

            _disposed = true;

            //Atomically get the final list of writers and clear the shared list
            //to prevent any further Subscribe or Broadcast operations from succeeding fully.
            var writersToCleanup = Interlocked.Exchange(ref _writers, new ConcurrentDictionary<long, ChannelWriter<TMessage>>());

            //Complete all channels that this broadcaster created and managed.
            //This signals all currently active subscribers that the source is gone.
            foreach (var (_, writer) in writersToCleanup)
            {
                writer.TryComplete();
            }
            // TODO: Replace with injected ILogger.
            Console.WriteLine($"[Pulsr] {nameof(Pulsr<TMessage>)} disposed. Completed {writersToCleanup.Count} remaining channels.");
        }

        /// <summary>
        /// Represents the subscription handle returned to the subscriber.
        /// Disposing this handle is crucial for unsubscribing and releasing resources.
        /// </summary>
        private sealed class Subscription : IDisposable
        {
            //holds a reference back to the singleton broadcaster instance.
            private Pulsr<TMessage>? _broadcaster;

            //We use 0 as the "disposed" sentinel value.
            private long _subscriptionId; 

            public Subscription(Pulsr<TMessage> broadcaster, long subscriptionId)
            {
                _broadcaster = broadcaster;
                _subscriptionId = subscriptionId;
            }

            public void Dispose()
            {
                //Atomically swap the current ID with our sentinel value (0).
                //This operation will return the *original* value of _subscriptionId before the swap.
                long idToUnsubscribe = Interlocked.Exchange(ref _subscriptionId, 0);

                //If the original value was not our sentinel (0), it means this is the first time Dispose() is being called.
                if (idToUnsubscribe != 0)
                {
                    _broadcaster?.Unsubscribe(idToUnsubscribe);
                    _broadcaster = null;
                }
            }
        }
    }
}
