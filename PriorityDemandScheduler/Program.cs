using System;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    class Program
    {
        static void Main(string[] args)
        {
            using var cts = new CancellationTokenSource();

            int N = 200;
            var scheduler = new Scheduler(Environment.ProcessorCount, cts.Token);


            var workers = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(idx => new Worker(scheduler, idx))
                .ToArray();

            var workerTasks = workers
                .Select(w => w.RunLoop(cts.Token))
                .ToArray();

            var tasks = new Task<double>[N];
            for (int i = 0; i < 200; ++i)
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
                    Console.WriteLine($"Completed task for job {idx}");
                    return acc;
                });
                tasks[idx] = task;
            }

            Task.WaitAll(tasks);

            Console.WriteLine("All tasks complete; shutting down");
            cts.Cancel();

            Task.WaitAll(workerTasks);
            Console.WriteLine("Shutdown complete");
        }
    }
}
