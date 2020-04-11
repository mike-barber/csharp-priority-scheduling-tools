using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    public class PriorityQueue
    {
        public readonly int Priority;
        public Dictionary<int, Queue<Task>> ThreadTasks;

        public PriorityQueue(int threads, int prio)
        {
            Priority = prio;

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
        readonly SortedList<int, PriorityQueue> _priorityQueues;
        readonly TaskCompletionSource<Task>[] _waiting;
        readonly int _numThreads;

        public Scheduler(int threads, CancellationToken ct)
        {
            _numThreads = threads;
            _priorityQueues = new SortedList<int, PriorityQueue>();
            _waiting = new TaskCompletionSource<Task>[threads];

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
        private bool TryGetNext(int threadIndex, out Task returnedTask)
        {
            returnedTask = null;

            // go through the queues in priority-order, lowest first (it's a sorted list)
            foreach (var queue in _priorityQueues.Values)
            {
                // get job for this thread
                {
                    if (queue.ThreadTasks[threadIndex].TryDequeue(out var task))
                    {
                        returnedTask = task;
                        Debug.Assert(task != null);
                        return true;
                    }
                }

                // otherwise steal a job for another thread
                foreach (var tq in queue.ThreadTasks.Values)
                {
                    if (tq.TryDequeue(out var task))
                    {
                        returnedTask = task;
                        Debug.Assert(task != null);
                        Console.WriteLine("Stolen immediate");
                        return true;
                    }
                }
            }

            return false;
        }

        // inside lock
        private void AssignTasksToWaiting()
        {
            // go through the queues in priority-order, lowest first (it's a sorted list)
            foreach (var queue in _priorityQueues.Values)
            {
                // non-stolen
                for (var threadIndex = 0; threadIndex < _waiting.Length; ++threadIndex)
                {
                    var tcs = _waiting[threadIndex];
                    if (tcs == null)
                        continue;

                    if (queue.ThreadTasks[threadIndex].TryDequeue(out var task))
                    {
                        // set result on waiter, and clear slot
                        tcs.SetResult(task);
                        //Console.WriteLine($"Waiter for {threadIndex} assigned task: {task.Id}");
                        _waiting[threadIndex] = null;
                    }
                }

                // stolen
                for (var threadIndex = 0; threadIndex < _waiting.Length; ++threadIndex)
                {
                    var tcs = _waiting[threadIndex];
                    if (tcs == null)
                        continue;

                    foreach (var q in queue.ThreadTasks.Values)
                    {
                        if (q.TryDequeue(out var task))
                        {
                            // set result on waiter and clear slot
                            Console.WriteLine("Stolen assigned");
                            tcs.SetResult(task);
                            _waiting[threadIndex] = null;
                        }
                    }
                }

                //Console.WriteLine("No tasks available");
            }
        }


        public Task<Task> GetNextJob(int threadIndex)
        {
            lock (_lk)
            {
                // if a task is available right now, return that
                if (TryGetNext(threadIndex, out var task))
                {
                    //Console.WriteLine($"Immediate task for {threadIndex}: {task.Id}");
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
                // enqueue the task, creating a new priority class if required
                var task = new Task<T>(function);
                if (!_priorityQueues.TryGetValue(priority, out var queue))
                {
                    _priorityQueues[priority] = queue = new PriorityQueue(_numThreads, priority);
                }
                queue.ThreadTasks[threadAffinity].Enqueue(task);

                // assign queued tasks to any workers waiting
                AssignTasksToWaiting();

                // return the task we've just queued
                return task;
            }
        }
    }
}
