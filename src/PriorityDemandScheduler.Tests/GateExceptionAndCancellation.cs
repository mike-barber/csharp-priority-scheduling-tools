using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PriorityDemandScheduler.Tests
{
    public class GateExceptionAndCancellation
    {
        public class MyException : Exception
        {
            public MyException() { }
        }

        int NumThreads = 4;

        [Fact]
        public void ExceptionTestAsync()
        {
            using var cts = new CancellationTokenSource();
            var scheduler = new GateScheduler(NumThreads);

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldThrows = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var thread = i % NumThreads;
                var shouldThrow = i % 5 == 0;

                shouldThrows[i] = shouldThrow;
                tasks[i] = scheduler.GatedRun(prio, async (gate) =>
                {
                    await gate.PermitYield();

                    if (shouldThrow)
                        throw new MyException();

                    return index;
                });
            }

            // some tasks should throw
            for (var i = 0; i < N; ++i)
            {
                if (shouldThrows[i])
                {
                    Assert.Throws<MyException>(() => tasks[i].GetAwaiter().GetResult());
                }
                else
                {
                    Assert.Equal(i, tasks[i].Result);
                }
            }
        }


        [Fact]
        public void TaskCancellationTestAsync()
        {
            using var ctsScheduler = new CancellationTokenSource();

            using var ctsTask = new CancellationTokenSource();
            ctsTask.Cancel();

            var scheduler = new GateScheduler(NumThreads);

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldCancels = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var thread = i % NumThreads;
                var shouldCancel = i % 5 == 0;
                var token = ctsTask.Token;

                shouldCancels[i] = shouldCancel;

                tasks[i] = scheduler.GatedRun(prio, async (gate) =>
                {
                    await gate.PermitYield();

                    // throw INSIDE task
                    if (shouldCancel)
                        token.ThrowIfCancellationRequested();

                    return index;
                });
            }

            // some tasks should throw
            for (var i = 0; i < N; ++i)
            {
                if (shouldCancels[i])
                {
                    // task should be cancelled WHILE running.
                    // NOTE: in the async case, we expect the task to be CANCELLED, not FAULTED, even though 
                    //       the exception was thrown from the task itself; this contradicts the non-async task
                    //       case. This matches the behaviour of Task.Run(async () xxx)
                    Assert.Throws<OperationCanceledException>(() => tasks[i].GetAwaiter().GetResult());
                    Assert.True(tasks[i].IsCanceled);
                    Assert.False(tasks[i].IsFaulted);
                }
                else
                {
                    Assert.Equal(i, tasks[i].Result);
                }
            }

            ctsScheduler.Cancel();
        }

        [Fact]
        public void PreCancellationTestAsync()
        {
            using var ctsScheduler = new CancellationTokenSource();

            using var ctsTask = new CancellationTokenSource();
            ctsTask.Cancel();

            var scheduler = new GateScheduler(NumThreads);

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldCancels = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var thread = i % NumThreads;
                var shouldCancel = i % 5 == 0;
                var token = ctsTask.Token;

                shouldCancels[i] = shouldCancel;
                var cancellationToken = shouldCancel ? token : CancellationToken.None;
                tasks[i] = scheduler.GatedRun(prio, async (gate) =>
                {
                    // should not be here if we should cancel prior
                    Assert.False(shouldCancel, "throw inside the task; should never get here");

                    await gate.PermitYield();

                    // complete successfully
                    return index;
                },
                // set cancellation token on the task -- this should cancel the task before it starts
                cancellationToken); 
            }

            // some tasks should throw
            for (var i = 0; i < N; ++i)
            {
                if (shouldCancels[i])
                {
                    // task should be cancelled BEFORE running, meaning it's CANCELLED, not FAULTED
                    Assert.Throws<TaskCanceledException>(() => tasks[i].GetAwaiter().GetResult());
                    Assert.True(tasks[i].IsCanceled);
                    Assert.False(tasks[i].IsFaulted);
                }
                else
                {
                    Assert.Equal(i, tasks[i].Result);
                }
            }

            ctsScheduler.Cancel();
        }
    }
}
