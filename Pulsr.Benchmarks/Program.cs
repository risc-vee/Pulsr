using BenchmarkDotNet.Running;

namespace Pulsr.Benchmarks
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<PulsrScalingConcurrentBenchmark>();
        }
    }
}
