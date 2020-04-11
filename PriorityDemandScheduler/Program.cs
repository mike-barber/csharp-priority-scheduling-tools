using System;
using System.Reflection.Metadata.Ecma335;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    class Program
    {
        static void Main(string[] args)
        {
            int N = 200;
            var scheduler = new Scheduler();

            var tasks = new Task<double>[N];
            for (int i=0; i<200; ++i)
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
                    Console.WriteLine($"Completed task {i}");
                    return acc;
                });
                tasks[i] = task;
            }

            Task.WaitAll(tasks);


        }
    }
}
