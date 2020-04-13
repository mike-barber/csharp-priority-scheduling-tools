using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler.Example
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await ExampleGate();
            await ExamplePriorityScheduler();
        }

        static async Task ExampleGate()
        {
            using var cts = new CancellationTokenSource();

            const int Prios = 3;
            const int PerThread = 20;

            var gateScheduler = new GateScheduler(Environment.ProcessorCount);

            // counts
            var counts = new SortedList<int, int>[Environment.ProcessorCount];
            for (int i = 0; i < Environment.ProcessorCount; ++i)
                counts[i] = new SortedList<int, int>();

            var lk = new object();
            void AddCount(int affinity, int threadId)
            {
                lock (lk)
                {
                    var c = counts[affinity];
                    if (!c.ContainsKey(threadId)) c[threadId] = 0;
                    c[threadId] += 1;
                }
            }

            var tasks = new List<Task<double>>();
            foreach (var p in Enumerable.Range(0, Prios).Reverse())
            {
                // add them in slowly -- watch for tasks getting pre-empted by more important ones
                Thread.Sleep(250);
                Console.WriteLine($"-- Starting prio {p}");

                foreach (var t in Enumerable.Range(0, Environment.ProcessorCount))
                {
                    var thread = t;

                    var task = gateScheduler.GatedRun(p, async gate =>
                    {
                        var priority = p;

                        static double Expensive()
                        {
                            double acc = 0;
                            for (int x = 0; x < 1e6; ++x)
                            {
                                acc += Math.Log(x + 1);
                            }
                            return acc;
                        }

                        double total = 0;
                        for (int i = 0; i < PerThread; ++i)
                        {
                            // yield to higher priority here if required
                            await gate.PermitYield();

                            // run the expensive operation
                            total += Expensive();
                            Console.WriteLine($"Completed task for job {i} on thread {Thread.CurrentThread.ManagedThreadId} for original affinity {thread} priority {priority} gate id {gate.Id}");
                            AddCount(thread, Thread.CurrentThread.ManagedThreadId);
                        }
                        return total;
                    });

                    tasks.Add(task);
                }
            }

            await Task.WhenAll(tasks);

            Console.WriteLine("All tasks complete; shutting down");

            var offMainThreadCount = 0;
            foreach (var c in counts)
            {
                Console.WriteLine(string.Join("\t", c.OrderByDescending(cc => cc.Value)));
                offMainThreadCount += c.OrderByDescending(cc => cc.Value).Skip(1).Sum(cc => cc.Value);
            }
            Console.WriteLine("--------------------------------------------------------------------------------------------------------");
        }


        static async Task ExamplePriorityScheduler()
        {
            using var cts = new CancellationTokenSource();

            int N = 500;
            var scheduler = new OrderingScheduler(Environment.ProcessorCount, cts.Token);


            // counts
            var counts = new SortedList<int, int>[Environment.ProcessorCount];
            for (int i = 0; i < Environment.ProcessorCount; ++i)
                counts[i] = new SortedList<int, int>();

            var lk = new object();
            void AddCount(int affinity, int threadId)
            {
                lock (lk)
                {
                    var c = counts[affinity];
                    if (!c.ContainsKey(threadId)) c[threadId] = 0;
                    c[threadId] += 1;
                }
            }

            // wait for the workers to spin up (testing)
            Thread.Sleep(250);

            var tasks = new Task<double>[N];
            for (int i = 0; i < N; ++i)
            {
                var idx = i;
                var threadAffinity = i % Environment.ProcessorCount;
                var priority = i % 3; // interleave tasks
                var task = scheduler.Run(priority, threadAffinity, () =>
                {
                    double acc = 0.0;
                    for (int x = 0; x < 1e6; ++x)
                    {
                        acc += Math.Log(x + 1);
                    }
                    Console.WriteLine($"Completed task for job {idx} on thread {Thread.CurrentThread.ManagedThreadId} for original affinity {threadAffinity} priority {priority}");
                    AddCount(threadAffinity, Thread.CurrentThread.ManagedThreadId);
                    return acc;
                });
                tasks[idx] = task;
            }

            await Task.WhenAll(tasks);

            Console.WriteLine("All tasks complete; shutting down");
            cts.Cancel();

            await scheduler.WaitForShutdown();
            Console.WriteLine("Shutdown complete");

            var offMainThreadCount = 0;
            foreach (var c in counts)
            {
                Console.WriteLine(string.Join("\t", c.OrderByDescending(cc => cc.Value)));
                offMainThreadCount += c.OrderByDescending(cc => cc.Value).Skip(1).Sum(cc => cc.Value);
            }
            var (pref, stol) = scheduler.GetCompletedCounts();
            Console.WriteLine($"Total {pref + stol}, intended preferred thread {pref}, stolen {stol}");
            Console.WriteLine($"Number actually executed off preferred thread: {offMainThreadCount}");
            Console.WriteLine("--------------------------------------------------------------------------------------------------------");
        }
    }
}
