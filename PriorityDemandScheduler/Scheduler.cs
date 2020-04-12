using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    public class Scheduler
    {
        readonly object _lk = new object();
        readonly SortedList<int,PriorityQueue> _priorityQueues;
        readonly TaskCompletionSource<Future>[] _waiting;
        readonly int _numThreads;

        public Scheduler(int threads, CancellationToken ct)
        {
            _numThreads = threads;
            _priorityQueues = new SortedList<int, PriorityQueue>();
            _waiting = new TaskCompletionSource<Future>[threads];

            // cancel all waiting jobs if the token is triggered
            ct.Register(() => CancelWaits(ct));
        }

        private void CancelWaits(CancellationToken ct)
        {
            lock (_lk)
            {
                for (var ti = 0; ti < _waiting.Length; ++ti)
                {
                    var wt = _waiting[ti];
                    if (wt == null)
                        continue;

                    if (wt.TrySetCanceled(ct))
                    {
                        Console.WriteLine($"Cancelled waiting: thread {ti} waiting {wt}");
                    }
                    else
                    {
                        Console.WriteLine($"Could not cancel: thread {ti} waiting {wt}");
                    }
                }
            }
        }

        // inside lock
        private bool TryGetNext(int threadIndex, out Future returnedFuture)
        {
            returnedFuture = null;

            // go through the queues in priority-order, lowest first (it's a sorted list)
            foreach (var queue in _priorityQueues.Values)
            {
                // get job for this thread
                {
                    if (queue.ThreadedJobs[threadIndex].TryDequeue(out var fut))
                    {
                        returnedFuture = fut;
                        Debug.Assert(fut != null);
                        return true;
                    }
                }

                // otherwise steal a job for another thread, starting with adjacent, round robin
                for (var i = 1; i < _numThreads; ++i)
                {
                    var otherThreadIdx = (threadIndex + i) % _numThreads;
                    if (queue.ThreadedJobs[otherThreadIdx].TryDequeue(out var fut))
                    {
                        returnedFuture = fut;
                        Debug.Assert(fut != null);
                        Console.WriteLine($"Stolen immediate: {threadIndex} stole job from {otherThreadIdx}");
                        return true;
                    }
                }
            }
            return false;
        }

        // inside lock
        private List<(Future, TaskCompletionSource<Future>)> AssignJobsToWaiting()
        {
            // simple approach -- assign tasks using TryGetNext
            List<(Future, TaskCompletionSource<Future>)> assignments = null;
            for (var threadIdx = 0; threadIdx < _waiting.Length; ++threadIdx)
            {
                var wt = _waiting[threadIdx];
                if (wt == null)
                    continue;

                // if a job is available, add it to the assignments list
                if (TryGetNext(threadIdx, out var fut))
                {
                    // lazily create assignments list if required
                    if (assignments == null) 
                        assignments = new List<(Future, TaskCompletionSource<Future>)>();
                    // record assignment
                    assignments.Add((fut, wt));
                    // clear from waiting list
                    _waiting[threadIdx] = null;
                }
                
            }
            return assignments;
        }


        public Task<Future> GetNextJob(int threadIndex)
        {
            lock (_lk)
            {
                // if a task is available right now, return that
                if (TryGetNext(threadIndex, out var fut))
                {
                    return Task.FromResult(fut);
                }

                // failing that, return a task completion source -- we'll hit this when a job arrives
                var tcs = new TaskCompletionSource<Future>();
                _waiting[threadIndex] = tcs;
                return tcs.Task;
            }
        }

        public Task<T> Run<T>(int priority, int threadAffinity, Func<T> function)
        {
            var fut = new Future<T>(function);
            List<(Future, TaskCompletionSource<Future>)> assignments = null;
            lock (_lk)
            {
                // enqueue the task, creating a new priority class if required
                if (!_priorityQueues.TryGetValue(priority, out var queue))
                {
                    _priorityQueues[priority] = queue = new PriorityQueue(_numThreads, priority);
                }
                queue.ThreadedJobs[threadAffinity].Enqueue(fut);

                // assign queued jobs to any workers waiting, but don't apply while we're still in the lock
                assignments = AssignJobsToWaiting();
            }

            if (assignments != null)
            {
                // now apply results to waiting jobs (outside the lock)
                foreach (var (f, tcs) in assignments)
                {
                    //tcs.SetResult(f);
                    // notify task completions: asynchronously, otherwise we just land up running them 
                    // on this thread
                    var completion = tcs;
                    var future = f;
                    Task.Run(() => completion.SetResult(future));
                }
            }

            // return the task for the job we've just queued
            return fut.CompletionSource.Task;
        }
    }
}
