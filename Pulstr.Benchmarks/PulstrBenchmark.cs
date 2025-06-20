using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Pulstr.Benchmarks
{
    public readonly record struct MyEvent(int Value);

    [ThreadingDiagnoser()]
    [MemoryDiagnoser]
    [ShortRunJob] // Use a shorter run for the very slow 10k subscriber case.
    [Description("Unified benchmark to test concurrent performance across a range of subscriber counts.")]
    public class PulstrScalingConcurrentBenchmark
    {
        // --- Workload Configuration ---
        // We'll run a fixed number of operations to see how the time changes as subscribers increase.
        private const int BroadcastsPerRun = 50;
        private const int ChurnsPerRun = 100; // A churn is one subscribe + one unsubscribe

        // This must be a compile-time constant for the attribute.
        private const int TotalOperations = BroadcastsPerRun + ChurnsPerRun; // = 110

        // --- Benchmark State ---
        private RobustPulstr<MyEvent> _pulstr;
        private readonly MyEvent _eventToBroadcast = new(42);

        // --- Parameters for Scaling ---

        [Params(10, 1000, 10_000)]
        public int SubscriberCount { get; set; }

        [Params(1, 4)]
        public int BroadcasterThreads { get; set; }

        [Params(1, 4)]
        public int ChurnThreads { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            // This setup runs for each combination of parameters.
            // It will be very fast for SubscriberCount=10 and noticeably slower for SubscriberCount=10000.
            _pulstr = new RobustPulstr<MyEvent>();
            for (int i = 0; i < SubscriberCount; i++)
            {
                var (_, subscription) = _pulstr.Subscribe();
            }
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _pulstr.Dispose();
        }

        [Benchmark(OperationsPerInvoke = TotalOperations)]
        public Task ConcurrentBroadcastAndChurn()
        {
            int broadcastsPerThread = BroadcastsPerRun / BroadcasterThreads;
            int churnsPerThread = ChurnsPerRun / ChurnThreads;

            var tasks = new List<Task>(BroadcasterThreads + ChurnThreads);

            // Create tasks for broadcasting threads
            for (int i = 0; i < BroadcasterThreads; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    for (int j = 0; j < broadcastsPerThread; j++)
                    {
                        await _pulstr.BroadcastAsync(_eventToBroadcast);
                    }
                }));
            }

            // Create tasks for churning threads
            for (int i = 0; i < ChurnThreads; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int j = 0; j < churnsPerThread; j++)
                    {
                        var (_, subscription) = _pulstr.Subscribe();
                        subscription.Dispose();
                    }
                }));
            }

            return Task.WhenAll(tasks);
        }
    }
}
