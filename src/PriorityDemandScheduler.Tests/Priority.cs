using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PriorityDemandScheduler.Tests
{
    public class Priority
    {
        [Fact]
        public void LowestPrioNumberCompletesFirst()
        {
            int PrioLevels = 5;
            int NumThreads = 4;
            int N = 500;

            using var cts = new CancellationTokenSource();

            var scheduler = new PriorityScheduler(NumThreads, cts.Token);

            // set up 3 sets of prioritised tasks; and queue the least important 
            // (highest prio number) first to make life more difficult.
            Task<(int, int)>[][] tasks = new Task<(int,int)>[PrioLevels][];
            for (int p = PrioLevels - 1; p >= 0; --p)
            {
                var prio = p;

                tasks[prio] = Enumerable.Range(0, N).Select(i =>
                {
                    var idx = i;
                    var thread = i % NumThreads;
                    return scheduler.Run(prio, thread, () =>
                    {
                        Thread.Sleep(1);
                        return (prio,idx);
                    });
                }).ToArray();
            }

            // setup joins for each priority level; and use Stopwatch to accurately 
            // determine completion time for each prio level
            var sw = Stopwatch.StartNew();
            var completeTicks = new long[PrioLevels];
            var joins = tasks.Select((arr, idx) =>
            {
                var ii = idx;
                var join = Task.WhenAll(arr).ContinueWith(t =>
                {
                    completeTicks[ii] = sw.ElapsedTicks;
                });
                return join;
            }).ToArray();

            // specifically reverse the joins so that any weirdness where waiting on less important stuff
            // earlier in the array is revealed as an issue.
            Array.Reverse(joins);
            Task.WaitAll(joins);
            for (int i = 1; i < PrioLevels; ++i)
            {
                Assert.True(completeTicks[i] > completeTicks[i - 1]);
            }

            cts.Cancel();
            scheduler.WaitForShutdown();
        }
    }
}
