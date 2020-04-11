using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    public class PriorityQueue
    {
        public Dictionary<int, Queue<Task>> ThreadTasks;

        public PriorityQueue(int threads)
        {
            ThreadTasks = new Dictionary<int, Queue<Task>>();
            for (int i = 0; i < threads; ++i)
            {
                ThreadTasks[i] = new Queue<Task>();
            }
        }
    }

    public class Scheduler
    {
        readonly object _lk = new object();
        readonly PriorityQueue _queue;
        readonly TaskCompletionSource<Task>[] _waiting;

        public Scheduler(int threads)
        {
            _queue = new PriorityQueue(threads);
            _waiting = new TaskCompletionSource<Task>[threads];
        }

        // inside lock
        private bool TryGetNext(int threadIndex, out Task returnedTask)
        {
            returnedTask = null;

            // get job for this thread
            if (_queue.ThreadTasks[threadIndex].TryDequeue(out var task))
            {
                returnedTask = task;
                return true;
            }

            // otherwise steal a job for another thread
            foreach (var q in _queue.ThreadTasks.Values)
            {
                if (q.TryDequeue(out var stolenTask))
                {
                    returnedTask = task;
                    return true;
                }
            }

            return false;
        }

        // inside lock
        private void AssignTasksToWaiting()
        {
            // non-stolen
            for (var threadIndex = 0; threadIndex < _waiting.Length; ++threadIndex)
            {
                var tcs = _waiting[threadIndex];
                if (tcs == null) 
                    continue;
                
                if (_queue.ThreadTasks[threadIndex].TryDequeue(out var task))
                {
                    // set result on waiter, and clear slot
                    tcs.SetResult(task);
                    _waiting[threadIndex] = null;
                }
            }

            // stolen
            for (var threadIndex = 0; threadIndex < _waiting.Length; ++threadIndex)
            {
                var tcs = _waiting[threadIndex];
                if (tcs == null)
                    continue;

                foreach (var q in _queue.ThreadTasks.Values)
                {
                    if (q.TryDequeue(out var taskStolen))
                    {
                        // set result on waiter and clear slot
                        tcs.SetResult(taskStolen);
                        _waiting[threadIndex] = null;
                    }
                }
            }
        }


        public Task<Task> GetNextJob(int threadIndex)
        {
            lock (_lk)
            {
                // if a task is available right now, return that
                if (TryGetNext(threadIndex, out var task))
                {
                    return Task.FromResult(task);
                }

                // failing that, return a task completion source -- we'll hit this when a job arrives
                var tcs = new TaskCompletionSource<Task>();
                _waiting[threadIndex] = tcs;
                return tcs.Task;
            }
        }

        public Task<T> Run<T>(int priority, int threadAffinity, Func<T> function)
        {
            lock (_lk)
            {
                // enqueue the task
                var task = new Task<T>(function);
                _queue.ThreadTasks[threadAffinity].Enqueue(task);

                // assign queued tasks to any workers waiting
                AssignTasksToWaiting();

                // return the task we've just queued
                return task;
            }
        }
    }
}
