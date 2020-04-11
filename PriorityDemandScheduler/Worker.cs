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
                    var task = await _scheduler.GetNextJob(_threadIndex).ConfigureAwait(false);
                    Console.WriteLine($"\tWorker {_threadIndex} starting job {task.Id}");
                    task.Start();
                    await task.ConfigureAwait(false);
                    //task.RunSynchronously();
                    Console.WriteLine($"\tWorker {_threadIndex} completed job {task.Id}");
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"Caught exception: {exc}");
                }
            }
            Console.WriteLine($"\tWorker complete: {_threadIndex}");
        }
    }
}
