using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PrioritySchedulingTools.Tests
{
    /// <summary>
    /// Baseline reference for Task.Run
    /// </summary>
    public class TaskExceptionAndCancellation
    {
        public class MyException : Exception
        {
            public MyException() { }
        }

        [Fact]
        public void ExceptionTest()
        {
            using var cts = new CancellationTokenSource();

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldThrows = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var shouldThrow = i % 5 == 0;

                shouldThrows[i] = shouldThrow;
                tasks[i] = Task.Run(() =>
                {
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
                    Assert.True(tasks[i].IsFaulted);
                }
                else
                {
                    Assert.Equal(i, tasks[i].Result);
                    Assert.True(tasks[i].IsCompleted);
                }
            }
        }

        [Fact]
        public void ExceptionTestAsync()
        {
            using var cts = new CancellationTokenSource();

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldThrows = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var shouldThrow = i % 5 == 0;

                shouldThrows[i] = shouldThrow;
                tasks[i] = Task.Run(async () =>
                {
                    await Task.Delay(1);

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
                    Assert.True(tasks[i].IsFaulted);
                }
                else
                {
                    Assert.Equal(i, tasks[i].Result);
                }
            }
        }


        [Fact]
        public void TaskCancellationTest()
        {
            using var ctsScheduler = new CancellationTokenSource();

            using var ctsTask = new CancellationTokenSource();
            ctsTask.Cancel();

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldCancels = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var shouldCancel = i % 5 == 0;
                var token = ctsTask.Token;

                shouldCancels[i] = shouldCancel;

                tasks[i] = Task.Run(() =>
                {
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
                    // task should be cancelled WHILE running, meaning it's FAULTED, not CANCELLED
                    Assert.Throws<OperationCanceledException>(() => tasks[i].GetAwaiter().GetResult());
                    Assert.False(tasks[i].IsCanceled);
                    Assert.True(tasks[i].IsFaulted);
                }
                else
                {
                    Assert.Equal(i, tasks[i].Result);
                }
            }

            ctsScheduler.Cancel();
        }

        [Fact]
        public void TaskCancellationTestAsync()
        {
            using var ctsScheduler = new CancellationTokenSource();

            using var ctsTask = new CancellationTokenSource();
            ctsTask.Cancel();

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldCancels = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var shouldCancel = i % 5 == 0;
                var token = ctsTask.Token;

                shouldCancels[i] = shouldCancel;

                tasks[i] = Task.Run(async () =>
                {
                    await Task.Delay(1);
                    
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
                    //       case.
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
        public void PreCancellationTest()
        {
            using var ctsScheduler = new CancellationTokenSource();

            using var ctsTask = new CancellationTokenSource();
            ctsTask.Cancel();

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldCancels = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var shouldCancel = i % 5 == 0;
                var token = ctsTask.Token;

                shouldCancels[i] = shouldCancel;
                var cancellationToken = shouldCancel ? token : CancellationToken.None;
                tasks[i] = Task.Run(() =>
                {
                    // should not be here if we should cancel prior
                    Assert.False(shouldCancel, "throw inside the task; should never get here");

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

        [Fact]
        public void PreCancellationTestAsync()
        {
            using var ctsScheduler = new CancellationTokenSource();

            using var ctsTask = new CancellationTokenSource();
            ctsTask.Cancel();

            int N = 100;
            var tasks = new Task<int>[100];
            var shouldCancels = new bool[100];
            for (int i = 0; i < N; ++i)
            {
                var index = i;
                var prio = i % 3;
                var shouldCancel = i % 5 == 0;
                var token = ctsTask.Token;

                shouldCancels[i] = shouldCancel;
                var cancellationToken = shouldCancel ? token : CancellationToken.None;
                tasks[i] = Task.Run(async () =>
                {
                    await Task.Delay(1);

                    // should not be here if we should cancel prior
                    Assert.False(shouldCancel, "throw inside the task; should never get here");

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
