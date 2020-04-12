using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    public abstract class Future
    {
        public abstract void Run();
    }

    // simple future to execute; won't do anything until Run() is called explicitly
    public class Future<T> : Future
    {
        private readonly Func<T> _function;
        private readonly TaskCompletionSource<T> _completionSource;

        public Future(Func<T> function)
        {
            _function = function;
            _completionSource = new TaskCompletionSource<T>();
        }

        // run synchronously now
        public override void Run()
        {
            try
            {
                var res = _function();
                _completionSource.SetResult(res);
            }
            catch (Exception exc)
            {
                _completionSource.SetException(exc);
            }
        }

        // get the completion task that will complete after Run() is called
        public Task<T> GetTask() => _completionSource.Task;
    }
}
