﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler.Example
{
    class Program
    {
        static async Task Main(string[] args)
        {
            using var cts = new CancellationTokenSource();

            int N = 500;
            var scheduler = new PriorityScheduler(Environment.ProcessorCount, cts.Token);


            // counts
            var counts = new SortedList<int, int>[Environment.ProcessorCount];
            for (int i = 0; i < Environment.ProcessorCount; ++i)
                counts[i] = new SortedList<int, int>();

            var lk = new object();
            void AddCount(int affinity, int threadId)
            {
                lock (lk)
                {
                    var c = counts[affinity];
                    if (!c.ContainsKey(threadId)) c[threadId] = 0;
                    c[threadId] += 1;
                }
            }

            // wait for the workers to spin up (testing)
            Thread.Sleep(250);

            var tasks = new Task<double>[N];
            for (int i = 0; i < N; ++i)
            {
                var idx = i;
                var threadAffinity = i % Environment.ProcessorCount;
                var priority = i % 3; // interleave tasks
                var task = scheduler.Run(priority, threadAffinity, () =>
                {
                    double acc = 0.0;
                    for (int x = 0; x < 1e6; ++x)
                    {
                        acc += Math.Log(x + 1);
                    }
                    Console.WriteLine($"Completed task for job {idx} on thread {Thread.CurrentThread.ManagedThreadId} for original affinity {threadAffinity} priority {priority}");
                    AddCount(threadAffinity, Thread.CurrentThread.ManagedThreadId);
                    return acc;
                });
                tasks[idx] = task;
            }

            await Task.WhenAll(tasks);

            Console.WriteLine("All tasks complete; shutting down");
            cts.Cancel();

            await scheduler.WaitForShutdown();
            Console.WriteLine("Shutdown complete");

            var offMainThreadCount = 0;
            foreach (var c in counts)
            {
                Console.WriteLine(string.Join("\t", c.OrderByDescending(cc => cc.Value)));
                offMainThreadCount += c.OrderByDescending(cc => cc.Value).Skip(1).Sum(cc => cc.Value);
            }
            var (pref, stol) = scheduler.GetCompletedCounts();
            Console.WriteLine($"Total {pref+stol}, intended preferred thread {pref}, stolen {stol}");
            Console.WriteLine($"Number actually executed off preferred thread: {offMainThreadCount}");
        }
    }
}
