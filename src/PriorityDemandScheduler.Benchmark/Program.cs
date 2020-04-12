using BenchmarkDotNet.Running;
using System;

namespace PriorityDemandScheduler.Benchmark
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<SchedulerPerf>();
        }
    }
}
