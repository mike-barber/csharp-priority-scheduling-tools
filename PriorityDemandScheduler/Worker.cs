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

        public async Task RunLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var task = await _scheduler.GetNextJob(_threadIndex).ConfigureAwait(false);
                    try
                    {
                        task.Start();
                        await task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"\tWorker {_threadIndex} task cancelled.");
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"\tWorker {_threadIndex} waiting cancelled");
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
