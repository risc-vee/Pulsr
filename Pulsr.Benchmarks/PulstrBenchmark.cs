using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Pulsr.Benchmarks
{
    public readonly record struct MyEvent(int Value);

    [ThreadingDiagnoser]
    [MemoryDiagnoser]
    [ShortRunJob]
    [Description("Tests a realistic lifecycle: subscriber ramp-up and a high-churn burst, with concurrent broadcasting throughout.")]
    public class PulsrRampUpHighChurnBurstsBenchmark
    {
        // --- Workload Configuration ---
        private const int BroadcastDelayMs = 10; //Delay between broadcasts to simulate real-world pacing
        private readonly MyEvent _eventToBroadcast = new(42);

        // --- Benchmark State ---
        private Pulsr<MyEvent> _pulsr;

        // --- Parameters ---

        [Params(100, 1000, 10_000)]
        public int TargetSubscriberCount { get; set; }

        [Params(1, 4)]
        public int BroadcasterParallelTasks { get; set; }

        [Params(10, 25)] // Percentage of subscribers to churn in the burst
        public int ChurnBurstPercentage { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            _pulsr = new Pulsr<MyEvent>();
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _pulsr.Dispose();
        }

        [Benchmark]
        public async Task RealisticRampUpAndBurst()
        {
            var cts = new CancellationTokenSource();
            var liveSubscriptions = new List<IDisposable>(TargetSubscriberCount);

            // 1. START BACKGROUND BROADCASTERS
            // These tasks will run for the entire duration of the benchmark,
            // continuously broadcasting events while the subscriber list changes.
            var broadcasterTasks = new List<Task>(BroadcasterParallelTasks);
            for (int i = 0; i < BroadcasterParallelTasks; i++)
            {
                broadcasterTasks.Add(Task.Run(async () =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await _pulsr.BroadcastAsync(_eventToBroadcast);
                        //A small delay to prevent a tight loop and simulate a real message cadence
                        await Task.Delay(BroadcastDelayMs, cts.Token);
                    }
                }, cts.Token));
            }

            // 2. RAMP-UP PHASE
            // Incrementally add subscribers up to the target count.
            for (int i = 0; i < TargetSubscriberCount; i++)
            {
                var (_, subscription) = _pulsr.Subscribe();
                liveSubscriptions.Add(subscription);
            }

            // 3. CHURN BURST PHASE
            // Simulate a sudden, high-volatility event.
            int churnCount = TargetSubscriberCount * ChurnBurstPercentage / 100;

            // Unsubscribe burst: A portion of existing subscribers disconnect.
            for (int i = 0; i < churnCount; i++)
            {
                // Dispose subscriptions from the start of the list
                liveSubscriptions[i].Dispose();
            }

            // Subscribe burst: A new set of subscribers connects.
            for (int i = 0; i < churnCount; i++)
            {
                // We don't need to store these new subscriptions for this test.
                var (_, subscription) = _pulsr.Subscribe();
            }

            // 4. CLEANUP
            // Stop the background broadcasters and wait for them to finish.
            cts.Cancel();

            try
            {
                await Task.WhenAll(broadcasterTasks);
            }
            catch (TaskCanceledException)
            { }
            catch (OperationCanceledException)
            { }

            //Note: Remaining live subscriptions will be cleaned up by Pulsr.Dispose() in GlobalCleanup.
        }
    }
}
