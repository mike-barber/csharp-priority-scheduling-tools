using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    internal abstract class Future
    {
        public abstract void Run();
    }

    // simple future to execute; won't do anything until Run() is called explicitly
    internal class Future<T> : Future
    {
        private readonly Func<T> _function;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completionSource;


        public Future(Func<T> function, CancellationToken cancellationToken)
        {
            _function = function;
            _cancellationToken = cancellationToken;
            _completionSource = new TaskCompletionSource<T>();
        }

        // run synchronously now
        public override void Run()
        {
            // check if already has cancellation requested
            if (_cancellationToken.IsCancellationRequested)
                _completionSource.SetCanceled();

            // otherwise, attempt to run and record exception if it occurs
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
