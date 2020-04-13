using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace PriorityDemandScheduler.Benchmark
{
    [MemoryDiagnoser]
    public class SchedulerPerf : IDisposable
    {
        public int NumJobs = 500;
        public int NumIterations = 250_000;

        private OrderingScheduler _priorityScheduler;
        private CancellationTokenSource _cancellationTokenSource;

        public SchedulerPerf()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _priorityScheduler = new OrderingScheduler(Environment.ProcessorCount, _cancellationTokenSource.Token);
        }


        private double ExpensiveOperation()
        {
            double acc = 0.0;
            for (int x = 0; x < NumIterations; ++x)
            {
                acc += Math.Log(x + 1);
            }
            return acc;
        }


        [Benchmark]
        public async Task<double> TaskRun()
        {
            var tasks = new Task<double>[NumJobs];
            for (var i = 0; i<tasks.Length; ++i)
            {
                tasks[i] = Task.Run(() => ExpensiveOperation());
            }

            await Task.WhenAll(tasks);
            double res = 0;
            for (var i = 0; i < tasks.Length; ++i)
            {
                res += tasks[i].Result;
            }

            return res;
        }

        [Benchmark]
        public async Task<double> SchedulerRunNoPrio()
        {
            var tasks = new Task<double>[NumJobs];
            var threads = Environment.ProcessorCount;
            for (var i = 0; i < tasks.Length; ++i)
            {
                var preferredThread = i % threads;
                tasks[i] = _priorityScheduler.Run(0, preferredThread, () => ExpensiveOperation());
            }

            await Task.WhenAll(tasks);
            double res = 0;
            for (var i = 0; i < tasks.Length; ++i)
            {
                res += tasks[i].Result;
            }

            return res;
        }

        [Benchmark]
        public async Task<double> SchedulerRunPrio()
        {
            var tasks = new Task<double>[NumJobs];
            var threads = Environment.ProcessorCount;
            for (var i = 0; i < tasks.Length; ++i)
            {
                var preferredThread = i % threads;
                var priority = i % 3;
                tasks[i] = _priorityScheduler.Run(priority, preferredThread, () => ExpensiveOperation());
            }

            await Task.WhenAll(tasks);
            double res = 0;
            for (var i = 0; i < tasks.Length; ++i)
            {
                res += tasks[i].Result;
            }

            return res;
        }


        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource?.Dispose();
            _priorityScheduler.WaitForShutdown();
        }

    }
}
