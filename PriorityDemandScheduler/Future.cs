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

    public class Future<T> : Future
    {
        Func<T> _function;
        public TaskCompletionSource<T> CompletionSource;

        public Future(Func<T> function, TaskCompletionSource<T> completionSource)
        {
            _function = function;
            CompletionSource = completionSource;
        }

        public override void Run()
        {
            try
            {
                var res = _function();
                CompletionSource.SetResult(res);
            }
            catch (Exception exc)
            {
                CompletionSource.SetException(exc);
            }
        }
    }
}
