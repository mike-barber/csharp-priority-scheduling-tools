﻿using System;
using System.Collections.Generic;
using System.Text;

namespace PriorityDemandScheduler
{
    public class PriorityQueue
    {
        public readonly int Priority;
        public Dictionary<int, Queue<Future>> ThreadedJobs;

        public PriorityQueue(int threads, int prio)
        {
            Priority = prio;

            ThreadedJobs = new Dictionary<int, Queue<Future>>();
            for (int i = 0; i < threads; ++i)
            {
                ThreadedJobs[i] = new Queue<Future>();
            }
        }
    }
}
