using BenchmarkDotNet.Running;

namespace Pulstr.Benchmarks
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<PulstrScalingConcurrentBenchmark>();
        }
    }
}
