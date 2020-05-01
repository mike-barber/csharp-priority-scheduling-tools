using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using System;

namespace PrioritySchedulingTools.Benchmark
{
    class Program
    {
        static void Main(string[] args)
        {
            // in-process
            var job = Job.Default
                .WithToolchain(BenchmarkDotNet.Toolchains.InProcess.NoEmit.InProcessNoEmitToolchain.Instance);

            // usual config, but with GcServer enabled.
            //var job = Job.Default
            //    .WithGcServer(true);

            var config = DefaultConfig.Instance
                .AddJob(job);

            BenchmarkRunner.Run<SchedulerPerf>(config);
        }
    }
}
