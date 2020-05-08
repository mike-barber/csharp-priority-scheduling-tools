using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using System;

namespace PrioritySchedulingTools.Benchmark
{
    class Program
    {
        static void Main(string[] args)
        {
            var job = Job.Default
                .WithGcServer(true);

            // in-process
            //job = job.WithToolchain(BenchmarkDotNet.Toolchains.InProcess.NoEmit.InProcessNoEmitToolchain.Instance);

            // limited iterations
            job = job
                .WithIterationCount(15)
                .WithIterationTime(TimeInterval.FromMilliseconds(500));

            var config = DefaultConfig.Instance.AddJob(job);
            BenchmarkRunner.Run<SchedulerPerf>(config);
        }
    }
}
