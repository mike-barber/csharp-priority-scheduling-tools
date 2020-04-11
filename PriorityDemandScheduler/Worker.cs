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

        async Task RunLoop(CancellationToken cts) 
        {
            while (!cts.IsCancellationRequested)
            {
                var job = await _scheduler.GetNextJob(_threadIndex);
                job.RunSynchronously();
            }
        }
    }
}
