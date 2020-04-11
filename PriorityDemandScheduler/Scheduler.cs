using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    public class PriorityQueue
    {
        public Dictionary<int, Queue<Task>> ThreadTasks;

        public PriorityQueue(int threads)
        {
            ThreadTasks = new Dictionary<int, Queue<Task>>();
            for (int i=0; i<threads; ++i)
            {
                ThreadTasks[i] = new Queue<Task>();
            }
        }
    }

    public class Scheduler
    {
        PriorityQueue _queue;
        Dictionary<int, TaskCompletionSource<Task>> _waiting = new Dictionary<int, TaskCompletionSource<Task>>();
       
        public Scheduler(int threads)
        {
            _queue = new PriorityQueue(threads);
        }

        public Task<Task> GetNextJob(int threadIndex)
        {
            // get job for this thread
            if (_queue.ThreadTasks[threadIndex].TryDequeue(out var task))
            {
                return Task.FromResult(task);
            }

            // otherwise steal a job for another thread
            foreach (var q in _queue.ThreadTasks.Values)
            {
                if (q.TryDequeue(out var stolenTask))
                {
                    return Task.FromResult(stolenTask);
                }
            }

            // failing that, return a task completion source -- we'll hit this when a job arrives
            var tcs = new TaskCompletionSource<Task>();
            _waiting[threadIndex] = tcs;
            return tcs.Task;
        }

        internal Task<T> Run<T>(int priority, int threadAffinity, Func<T> function)
        {
            throw new NotImplementedException();
        }
    }
}
