using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    public class PriorityQueue
    {
        public Dictionary<int, Queue<Future>> ThreadedJobs;

        public PriorityQueue(int threads)
        {
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
        readonly PriorityQueue _queue;
        readonly TaskCompletionSource<Future>[] _waiting;

        public Scheduler(int threads, CancellationToken ct)
        {
            _queue = new PriorityQueue(threads);
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

            {
                // get job for this thread
                if (_queue.ThreadedJobs[threadIndex].TryDequeue(out var fut))
                {
                    returnedFuture = fut;
                    Debug.Assert(fut != null);
                    return true;
                }
            }

            // otherwise steal a job for another thread
            foreach (var q in _queue.ThreadedJobs.Values)
            {
                if (q.TryDequeue(out var fut))
                {
                    returnedFuture = fut;
                    Debug.Assert(fut != null);
                    Console.WriteLine("Stolen immediate");
                    return true;
                }
            }

            return false;
        }

        // inside lock
        private void AssignJobsToWaiting()
        {
            // non-stolen
            for (var threadIndex = 0; threadIndex < _waiting.Length; ++threadIndex)
            {
                var tcs = _waiting[threadIndex];
                if (tcs == null)
                    continue;

                if (_queue.ThreadedJobs[threadIndex].TryDequeue(out var fut))
                {
                    // set result on waiter, and clear slot
                    tcs.SetResult(fut);
                    _waiting[threadIndex] = null;
                }
            }

            // stolen
            for (var threadIndex = 0; threadIndex < _waiting.Length; ++threadIndex)
            {
                var tcs = _waiting[threadIndex];
                if (tcs == null)
                    continue;

                foreach (var q in _queue.ThreadedJobs.Values)
                {
                    if (q.TryDequeue(out var fut))
                    {
                        // set result on waiter and clear slot
                        Console.WriteLine("Stolen assigned");
                        tcs.SetResult(fut);
                        _waiting[threadIndex] = null;
                    }
                }
            }
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
            lock (_lk)
            {
                // enqueue the job
                var tcs = new TaskCompletionSource<T>();
                var fut = new Future<T>(function, tcs);
                _queue.ThreadedJobs[threadAffinity].Enqueue(fut);

                // assign queued jobs to any workers waiting
                AssignJobsToWaiting();

                // return the task for the job we've just queued
                return tcs.Task;
            }
        }
    }
}
