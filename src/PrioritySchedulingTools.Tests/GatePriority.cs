using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PrioritySchedulingTools.Tests
{
    public class GatePriority
    {
        [Fact]
        public void MutiplePriority_LowestPrioNumberCompletesFirst()
        {
            int PrioLevels = 5;
            int NumThreads = 4;
            int N = 50;

            var scheduler = new GateScheduler(NumThreads);

            // set up 3 sets of prioritised tasks; and queue the least important 
            // (highest prio number) first to make life more difficult.
            Task<(int, int)>[][] tasks = new Task<(int,int)>[PrioLevels][];
            for (int p = PrioLevels - 1; p >= 0; --p)
            {
                int counter = -1;
                var prio = p;

                tasks[prio] = Enumerable.Range(0, NumThreads).Select(t =>
                {
                    var thread = t;
                    return scheduler.GatedRun(prio, async gate =>
                    {
                        int index;
                        while ((index = Interlocked.Increment(ref counter)) < N)
                        {
                            await gate.WaitToContinueAsync();
                            await Task.Delay(10);
                        }
                        return (prio, thread);
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
            Console.WriteLine("Completion times: " + string.Join(",", completeTicks));
            for (int i = 1; i < PrioLevels; ++i)
            {
                var correctOrder = completeTicks[i] > completeTicks[i - 1];
                Assert.True(correctOrder);
            }
        }

        [Fact]
        public async Task SinglePriority_TasksCompleteInOrder()
        {
            var scheduler = new GateScheduler(1);

            var syncStart = new TaskCompletionSource<int>();
            object lk = new object();
            var completionOrder = new List<int>();

            var t0 = scheduler.GatedRun(0, async gate => 
            {
                await syncStart.Task;
                await gate.WaitToContinueAsync();
                lock (lk)
                {
                    completionOrder.Add(0);
                }
            });
            var t1 = scheduler.GatedRun(0, async gate =>
            {
                await syncStart.Task;
                await gate.WaitToContinueAsync();
                lock (lk)
                {
                    completionOrder.Add(1);
                }
            });
            var t2 = scheduler.GatedRun(0, async gate =>
            {
                await syncStart.Task;
                await gate.WaitToContinueAsync();
                lock (lk)
                {
                    completionOrder.Add(2);
                }
            });

            // now release all to start, and wait for them to finish
            syncStart.SetResult(0);
            await Task.WhenAll(t0, t1, t2);

            Assert.Equal(0, completionOrder[0]);
            Assert.Equal(1, completionOrder[1]);
            Assert.Equal(2, completionOrder[2]);
        }
    }
}
