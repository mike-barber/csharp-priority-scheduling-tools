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

                // otherwise steal a job for another thread
                foreach (var q in queue.ThreadedJobs.Values)
                {
                    if (q.TryDequeue(out var fut))
                    {
                        returnedFuture = fut;
                        Debug.Assert(fut != null);
                        Console.WriteLine("Stolen immediate");
                        return true;
                    }
                }
            }
            return false;
        }

        // inside lock
        private List<(Future,TaskCompletionSource<Future>)> AssignJobsToWaiting()
        {
            // count number of waiting workers
            var numWaiting = 0;
            foreach (var wt in _waiting)
            {
                if (wt != null) numWaiting++;
            }
            if (numWaiting == 0) 
                return null;

            Console.WriteLine($"Waiting: {numWaiting}");

            // create list of assignments
            var assignments = new List<(Future, TaskCompletionSource<Future>)>(numWaiting);

            // go through the queues in priority-order, lowest first (it's a sorted list)
            foreach (var queue in _priorityQueues.Values)
            {
                if (numWaiting == 0) break;

                // non-stolen
                for (var threadIndex = 0; threadIndex < _waiting.Length; ++threadIndex)
                {
                    if (numWaiting == 0) break;

                    var tcs = _waiting[threadIndex];
                    if (tcs == null)
                        continue;

                    if (queue.ThreadedJobs[threadIndex].TryDequeue(out var fut))
                    {
                        // set result on waiter, and clear slot
                        //tcs.SetResult(fut);
                        assignments.Add((fut, tcs));
                        _waiting[threadIndex] = null;
                        numWaiting--;
                    }
                }

                // stolen
                for (var threadIndex = 0; threadIndex < _waiting.Length; ++threadIndex)
                {
                    if (numWaiting == 0) break;

                    var tcs = _waiting[threadIndex];
                    if (tcs == null)
                        continue;

                    foreach (var q in queue.ThreadedJobs.Values)
                    {
                        if (q.TryDequeue(out var fut))
                        {
                            // set result on waiter and clear slot
                            Console.WriteLine("Stolen assigned");
                            //tcs.SetResult(fut);
                            assignments.Add((fut, tcs));
                            _waiting[threadIndex] = null;
                            numWaiting--;
                        }
                    }
                }
            }

            Console.WriteLine($"Assigned: {assignments.Count}");
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
