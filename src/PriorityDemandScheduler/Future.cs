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
        private readonly Func<T> _function;
        public readonly TaskCompletionSource<T> CompletionSource;

        public Future(Func<T> function)
        {
            _function = function;
            CompletionSource = new TaskCompletionSource<T>();
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
