using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace PrioritySchedulingTools.Tests
{
    public class PrioIdOrdering
    {
        // simple test to demonstrate that tuple ordering works as expected
        [Fact]
        public void PrioId_Ordering_Correct()
        {
            // for clarity
            PrioId Create(int prio, long id) => new PrioId(prio, id);

            var baseline = Create(1, 10);

            Assert.True(Create(1, 10) == baseline, "equal");
            
            Assert.True(Create(0, 10) < baseline, "'higher' priority");
            Assert.True(Create(2, 10) > baseline, "'lower' priority");
            
            Assert.True(Create(1, 9) < baseline, "same priority, earlier Id");
            Assert.True(Create(1, 11) > baseline, "same priority, later Id");
        }
    }
}
