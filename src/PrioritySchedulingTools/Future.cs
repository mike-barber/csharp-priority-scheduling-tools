using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrioritySchedulingTools
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
        private int _runCount = 0;

        public Future(Func<T> function, CancellationToken cancellationToken)
        {
            _function = function;
            _cancellationToken = cancellationToken;
            _completionSource = new TaskCompletionSource<T>();
        }

        // run synchronously now
        public override void Run()
        {
            // check for repeated invocation
            if (Interlocked.Increment(ref _runCount) != 1)
            {
                throw new InvalidOperationException("Cannot execute future more than once");
            }

            // check if already has cancellation requested
            if (_cancellationToken.IsCancellationRequested)
            {
                _completionSource.SetCanceled();
                return;
            }

            // otherwise, attempt to run and record exception if it occurs
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                var res = _function();
                _completionSource.SetResult(res);
            }
            catch (Exception exc)
            {
                _completionSource.SetException(exc);
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        // get the completion task that will complete after Run() is called
        public Task<T> GetTask() => _completionSource.Task;
    }
}
