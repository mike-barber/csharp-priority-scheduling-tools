using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrioritySchedulingTools
{
    public class OrderingScheduler
    {
        readonly object _lk = new object();
        readonly int _numThreads;
        readonly SortedList<int,PriorityQueue> _priorityQueues;
        readonly TaskCompletionSource<Future>[] _waiting;
        readonly Worker[] _workers;
        readonly Task<Task>[] _workerTasks;


        private long _stolenCount;
        private long _preferredCount;

        public OrderingScheduler(int threads, CancellationToken ct) : this(threads, TaskScheduler.Default, ct) { }

        public OrderingScheduler(int threads, TaskScheduler taskScheduler, CancellationToken ct)
        {
            _numThreads = threads;
            _priorityQueues = new SortedList<int, PriorityQueue>();
            _waiting = new TaskCompletionSource<Future>[threads];

            // create workers
            _workers = Enumerable.Range(0, threads)
                .Select(idx => new Worker(this, idx))
                .ToArray();

            // start worker tasks on long-running threads
            _workerTasks = _workers
                .Select(w =>
                {
                    var worker = w;
                    var task = Task.Factory.StartNew(() => worker.RunLoop(ct), 
                        CancellationToken.None, 
                        TaskCreationOptions.LongRunning,
                        taskScheduler);
                    return task;
                })
                .ToArray();

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

                    if (!wt.TrySetCanceled(ct))
                    {
                        Console.WriteLine($"WARNING: Could not cancel: thread {ti} waiting {wt}");
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
#if DIAGNOSTICS
                        Debug.Assert(fut != null);
#endif
                        _preferredCount++;
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
#if DIAGNOSTICS
                        Debug.Assert(fut != null);
                        Console.WriteLine($"Stolen: {threadIndex} stole job from {otherThreadIdx}");
#endif
                        _stolenCount++;
                        return true;
                    }
                }
            }
            return false;
        }

        // inside lock
        private void AssignJobsToWaiting()
        {
            // simple approach -- assign tasks using TryGetNext
            for (var threadIdx = 0; threadIdx < _waiting.Length; ++threadIdx)
            {
                var wt = _waiting[threadIdx];
                if (wt == null)
                    continue;

                // if a job is available, add it to the assignments list
                if (TryGetNext(threadIdx, out var fut))
                {
                    // asynchronously, on a separate task, set the completion; otherwise it'll continue on this thread
                    var completion = wt;
                    var future = fut;
                    Task.Run(() => completion.SetResult(future));
                    // clear from waiting list
                    _waiting[threadIdx] = null;
                }
            }
        }


        internal Task<Future> GetNextJob(int threadIndex)
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

        public Task<T> Run<T>(int priority, int threadAffinity, Func<T> function, CancellationToken cancellationToken = default)
        {
            var fut = new Future<T>(function, cancellationToken);
            lock (_lk)
            {
                // enqueue the task, creating a new priority class if required
                if (!_priorityQueues.TryGetValue(priority, out var queue))
                {
                    _priorityQueues[priority] = queue = new PriorityQueue(_numThreads, priority);
                }
                queue.ThreadedJobs[threadAffinity].Enqueue(fut);

                // assign queued jobs to any workers waiting, but don't apply while we're still in the lock
                AssignJobsToWaiting();
            }

            // return the task for the job we've just queued
            return fut.GetTask();
        }

        public Task WaitForShutdown()
        {
            return Task.WhenAll(_workerTasks);
        }

        // get completed counts
        public (long preferred, long stolen) GetCompletedCounts()
        {
            lock (_lk)
            {
                return (_preferredCount, _stolenCount);
            }
        }
    }
}
