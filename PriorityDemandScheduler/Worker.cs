using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    public class Worker
    {
        readonly Scheduler _scheduler;
        readonly int _threadIndex;

        public Worker(Scheduler scheduler, int threadIndex)
        {
            _scheduler = scheduler;
            _threadIndex = threadIndex;
        }

        public async Task RunLoop(CancellationToken cts)
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var job = await _scheduler.GetNextJob(_threadIndex);
                    if (job == null)
                    {
                        Console.WriteLine("null job");
                    }
                    job.RunSynchronously();
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"Caught exception: {exc}");
                }
            }
        }
    }
}
