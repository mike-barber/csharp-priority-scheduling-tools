using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using System;

namespace PriorityDemandScheduler.Benchmark
{
    class Program
    {
        static void Main(string[] args)
        {
            var job = Job.Default
                .WithToolchain(BenchmarkDotNet.Toolchains.InProcess.NoEmit.InProcessNoEmitToolchain.Instance);

            var config = DefaultConfig.Instance
                .AddJob(job);

            BenchmarkRunner.Run<SchedulerPerf>(config);
        }
    }
}
