using System.Collections.Immutable;
using System.Threading.Channels;

namespace Pulsr
{
    /// <summary>
    /// An in-memory publish-subscribe broadcaster that concurrently distributes messages
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
        private ImmutableList<ChannelWriter<TMessage>> _writers = ImmutableList<ChannelWriter<TMessage>>.Empty;

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

            var writer = channel.Writer;

            ImmutableInterlocked.Update(ref _writers, list => list.Add(writer));

            //The subscription handle knows how to remove this specific writer
            //from the shared broadcaster instance.
            var subscription = new Subscription(this, writer);

            return (channel.Reader, subscription);
        }

        /// <summary>
        /// Removes a subscriber's channel writer and completes it.
        /// Called internally by Subscription.Dispose(). Thread-safe.
        /// </summary>
        private void Unsubscribe(ChannelWriter<TMessage> writer)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Pulsr<TMessage>));

            ImmutableInterlocked.Update(ref _writers, list => list.Remove(writer));

            //Signal to the reader associated with this writer that no more items
            //will be coming *from this broadcaster*. Essential for graceful shutdown
            //of the subscriber's reading loop.
            writer.TryComplete();
        }

        /// <summary>
        /// Broadcasts an message to all currently subscribed channel writers.
        /// This method is thread-safe and can be called concurrently.
        /// </summary>
        /// <param name="ev">The message to broadcast.</param>
        public async ValueTask BroadcastAsync(TMessage ev)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Pulsr<TMessage>));

            //Take an atomic snapshot of the current list of writers.
            //This ensures we work with a consistent set for this specific broadcast.
            var currentWriters = _writers;
            //TODO: check how its snapshotted

            if (currentWriters.IsEmpty)
            {
                return;
            }
            if (currentWriters.Count == 1)
            {
                await WriteToChannelAsync(currentWriters[0], ev).ConfigureAwait(false);
                return;
            }

            var tasks = new Task[currentWriters.Count];
            for (int i = 0; i < currentWriters.Count; i++)
            {
                tasks[i] = WriteToChannelAsync(currentWriters[i], ev);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Helper to write to a single channel with appropriate error handling,
        /// especially for channels closed by subscribers.
        /// </summary>
        private async Task WriteToChannelAsync(ChannelWriter<TMessage> writer, TMessage ev)
        {
            try
            {
                await writer.WriteAsync(ev).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                //we don't want to throw an exception if the channel is closed by the subscriber.
            }
            catch (Exception ex)
            {
                //TODO: implement proper logging
                Console.WriteLine($"[Pulsr] Error writing message to subscriber channel: {ex.GetType().Name} - {ex.Message}");
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
            _disposed = true;

            //Atomically get the final list of writers and clear the shared list
            //to prevent any further Subscribe or Broadcast operations from succeeding fully.
            var writersToCleanup = Interlocked.Exchange(ref _writers, ImmutableList<ChannelWriter<TMessage>>.Empty);

            //Complete all channels that this broadcaster created and managed.
            //This signals all currently active subscribers that the source is gone.
            foreach (var writer in writersToCleanup)
            {
                writer.TryComplete();
            }
            //TODO: implement proper logging
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
            //holds the specific writer associated with this subscription.
            private ChannelWriter<TMessage>? _writer;

            public Subscription(Pulsr<TMessage> broadcaster, ChannelWriter<TMessage> writer)
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
                //Use Interlocked.Exchange to ensure thread-safe, idempotent disposal.
                //This retrieves the writer *once* and sets the field to null.
                var writerToUnsubscribe = Interlocked.Exchange(ref _writer, null);
                if (writerToUnsubscribe != null)
                {
                    //Call Unsubscribe on the potentially shared broadcaster instance.
                    _broadcaster?.Unsubscribe(writerToUnsubscribe);
                    //Release the reference to the broadcaster.
                    _broadcaster = null;
                }
            }
        }
    }
}
