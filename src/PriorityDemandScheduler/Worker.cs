using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    internal class Worker
    {
        readonly OrderingScheduler _scheduler;
        readonly int _threadIndex;

        public Worker(OrderingScheduler scheduler, int threadIndex)
        {
            _scheduler = scheduler;
            _threadIndex = threadIndex;
        }

        public async Task RunLoop(CancellationToken ct)
        {
            Console.WriteLine($"\tWorker {_threadIndex} started on thread {Thread.CurrentThread.ManagedThreadId}...");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var future = await _scheduler.GetNextJob(_threadIndex).ConfigureAwait(false);
                    try
                    {
                        // run it now on this thread
                        future.Run();
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
