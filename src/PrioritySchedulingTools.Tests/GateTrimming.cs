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
    public class GateTrimming
    {
        [Fact]
        public async Task ManyGatesAdded_TrimmingWorked()
        {
            int PrioLevels = 3;
            int NumThreads = 4;
            int N = 10_000;

            var scheduler = new GateScheduler(NumThreads);

            var tasks = Enumerable.Range(0, N).Select(i =>
            {
                var prio = i % PrioLevels;
                return scheduler.GatedRun(prio, async gate =>
                {
                    await gate.WaitToContinueAsync();
                    // no op 
                });
            });
            

            // wait for all to complete
            await Task.WhenAll(tasks);

            // check list total outstanding size -- should not exceed the TrimListAtSize completed constant
            Assert.InRange(scheduler.GetTotalListSize(), 0, GateScheduler.TrimListAtSizeCompleted * PrioLevels);
        }
    }
}
