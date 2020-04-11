using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    class Program
    {
        static async Task Main(string[] args)
        {
            using var cts = new CancellationTokenSource();

            int N = 500;
            var scheduler = new Scheduler(Environment.ProcessorCount, cts.Token);


            var workers = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(idx => new Worker(scheduler, idx))
                .ToArray();

            var workerTasks = workers
                .Select(w => Task.Run(() => w.RunLoop(cts.Token)))
                .ToArray();

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


            var tasks = new Task<double>[N];
            for (int i = 0; i < N; ++i)
            {
                var idx = i;
                var threadAffinity = i % Environment.ProcessorCount;
                var priority = 0;
                var task = scheduler.Run(priority, threadAffinity, () =>
                {
                    double acc = 0.0;
                    for (int x = 0; x < 1e6; ++x)
                    {
                        acc += Math.Log(x + 1);
                    }
                    Console.WriteLine($"Completed task for job {idx} on thread {Thread.CurrentThread.ManagedThreadId} for original affinity {threadAffinity}");
                    AddCount(threadAffinity, Thread.CurrentThread.ManagedThreadId);
                    return acc;
                });
                tasks[idx] = task;
            }

            await Task.WhenAll(tasks);

            Console.WriteLine("All tasks complete; shutting down");
            cts.Cancel();

            await Task.WhenAll(workerTasks);
            Console.WriteLine("Shutdown complete");

            foreach (var c in counts)
            {
                Console.WriteLine(string.Join("\t", c));
            }
        }
    }
}
