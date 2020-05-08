using BenchmarkDotNet.Attributes;
using Microsoft.Diagnostics.Tracing.Parsers.Tpl;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace PrioritySchedulingTools.Benchmark
{
    [MemoryDiagnoser]
    public class SchedulerPerf : IDisposable
    {
        public int PrioLevels = 3;

        public int ChunksPerProcessor = 50;
        public int JobsPerChunk = 250;
        public int NumIterations = 100;

        private OrderingScheduler _orderingScheduler;
        private GateScheduler _gateScheduler;

        private CancellationTokenSource _cancellationTokenSource;

        public SchedulerPerf()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _orderingScheduler = new OrderingScheduler(Environment.ProcessorCount, _cancellationTokenSource.Token);
            _gateScheduler = new GateScheduler(Environment.ProcessorCount);
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


        // one long list, no batches
        [Benchmark]
        public async Task<double> TaskRunSimplistic()
        {
            var tasks = new Task<double>[Environment.ProcessorCount * ChunksPerProcessor * JobsPerChunk];
            for (var i = 0; i < tasks.Length; ++i)
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
        public async Task<double> TaskRun()
        {
            var tasks = new Task<double>[Environment.ProcessorCount * ChunksPerProcessor];
            for (var i = 0; i < tasks.Length; ++i)
            {
                tasks[i] = Task.Run(() =>
                {
                    double total = 0.0;
                    for (var j = 0; j < JobsPerChunk; ++j)
                    {
                        total += ExpensiveOperation();
                    }
                    return total;
                });
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
        public async Task<double> TaskRunAsync()
        {
            static Task NoWait()
            {
                return Task.CompletedTask;
            }

            var tasks = new Task<double>[Environment.ProcessorCount * ChunksPerProcessor];
            for (var i = 0; i < tasks.Length; ++i)
            {
                tasks[i] = Task.Run(async () =>
                {
                    double total = 0.0;
                    for (var j = 0; j < JobsPerChunk; ++j)
                    {
                        await NoWait();
                        total += ExpensiveOperation();
                    }
                    return total;
                });
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
        public async Task<double> GateNoPrio()
        {
            var threads = Environment.ProcessorCount;
            var tasks = new List<Task<double>>(threads * ChunksPerProcessor);

            for (var c = 0; c < ChunksPerProcessor; ++c)
            {
                for (var i = 0; i < threads; ++i)
                {
                    tasks.Add(_gateScheduler.GatedRun(0, async gate =>
                    {
                        double total = 0.0;
                        for (var j = 0; j < JobsPerChunk; ++j)
                        {
                            await gate.WaitToContinueAsync();
                            total += ExpensiveOperation();
                        }
                        return total;
                    }));
                }
            }

            await Task.WhenAll(tasks);
            double res = 0;
            for (var i = 0; i < tasks.Count; ++i)
            {
                res += tasks[i].Result;
            }

            return res;
        }

        [Benchmark]
        public async Task<double> GatePrio()
        {
            var threads = Environment.ProcessorCount;
            var tasks = new List<Task<double>>(threads * ChunksPerProcessor);

            for (var c = 0; c < ChunksPerProcessor; ++c)
            {
                for (var i = 0; i < threads; ++i)
                {
                    var prio = c % PrioLevels;

                    tasks.Add(_gateScheduler.GatedRun(prio, async gate =>
                    {
                        double total = 0.0;
                        for (var j = 0; j < JobsPerChunk; ++j)
                        {
                            await gate.WaitToContinueAsync();
                            total += ExpensiveOperation();
                        }
                        return total;
                    }));
                }
            }

            await Task.WhenAll(tasks);
            double res = 0;
            for (var i = 0; i < tasks.Count; ++i)
            {
                res += tasks[i].Result;
            }

            return res;
        }

        [Benchmark]
        public async Task<double> OrderingNoPrio()
        {
            var tasks = new Task<double>[Environment.ProcessorCount * ChunksPerProcessor];
            var threads = Environment.ProcessorCount;
            for (var i = 0; i < tasks.Length; ++i)
            {
                var preferredThread = i % threads;
                tasks[i] = _orderingScheduler.Run(0, preferredThread, () =>
                {
                    double total = 0.0;
                    for (var j = 0; j < JobsPerChunk; ++j)
                    {
                        total += ExpensiveOperation();
                    }
                    return total;
                });
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
        public async Task<double> OrderingPrio()
        {
            var tasks = new Task<double>[Environment.ProcessorCount * ChunksPerProcessor];
            var threads = Environment.ProcessorCount;
            for (var i = 0; i < tasks.Length; ++i)
            {
                var preferredThread = i % threads;
                var priority = i % PrioLevels;
                tasks[i] = _orderingScheduler.Run(priority, preferredThread, () =>
                {
                    double total = 0.0;
                    for (var j = 0; j < JobsPerChunk; ++j)
                    {
                        total += ExpensiveOperation();
                    }
                    return total;
                });
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
            _orderingScheduler.WaitForShutdown();
        }

    }
}
