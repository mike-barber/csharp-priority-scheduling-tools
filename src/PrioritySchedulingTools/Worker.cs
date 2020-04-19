using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrioritySchedulingTools
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
            //Console.WriteLine($"\tWorker {_threadIndex} started on thread {Thread.CurrentThread.ManagedThreadId}...");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var future = await _scheduler.GetNextJob(_threadIndex).ConfigureAwait(false);
                    future.Run(); // run it now on this thread
                }
                catch (TaskCanceledException)
                {
                    // expected when GetNextJob is cancelled during shutdown
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"Caught unexpected exception: {exc}");
                }
            }
            //Console.WriteLine($"\tWorker complete: {_threadIndex}");
        }
    }
}
