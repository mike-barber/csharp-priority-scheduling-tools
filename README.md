# C# Job priority scheduling tools

Priorities are supplied as integers, with lower numbers having priority over higher numbers. So for a three-class problem, you'd could have:

0. High Priority
1. Medium Priority
2. Low Priority

You're free to use these any way you'd like, including negative numbers. However, a new queue is created for each new class, and is included in the scan for the most prioritised item, so having more than a few will lead to performance issues.

## Compute bound 

These tools are specifically designed for compute-bound workloads, especially given the explicit concurrency limits they implement. Do not use them for IO-bound workloads -- you'll cause an artificial bottleneck.

## `GatedScheduler`

The `GatedScheduler` allows you to run up to `N` concurrent tasks with as many priorities as required. 

Rather than handling the actual scheduling of tasks directly, the idea is that tasks are *interruptible*. The scheduler will give you a `gate`, and you call `await gate.WaitToContinueAsync();` inside your task. If this task can proceed immediately, it does. If another more important gate is waiting, you'll be delayed until it is complete, and the more important task will be allowed to proceed instead.

Tasks can run for some time, and you can insert several gate checks at various points as required. They're not free, given that the scheduler checks for other more important tasks and requires synchronisation, so you want to aim for some degree of chunkiness in the work you're doing between checks.

```C#
var tasks = new List<Task<double>>(threads * ChunksPerProcessor);

for (var c = 0; c < ChunksPerProcessor; ++c)
{
    for (var i = 0; i < threads; ++i)
    {
        tasks.Add(gateScheduler.GatedRun(0, async gate =>
        {
            double total = 0.0;
            for (var j = 0; j < JobsPerChunk; ++j)
            {
                // conditionally stop execution and let another task proceed
                await gate.WaitToContinueAsync();
                total += ExpensiveOperation();
            }
            return total;
        }));
    }
}
```

## `OrderingScheduler`

The `OrderingScheduler` allows up to `N` concurrent tasks, and each task is assigned a priority and a preferred thread. This works well for a large number of queued tasks that don't have much context, and is designed to work similarly to `Task.Run` for the user.

The tasks will be run in priority-order, and on worker tasks that correspond to the "thread"; there are `N` worker tasks that the scheduler runs, and supplied tasks are run by these worker tasks, preferentially on the supplied "thread". This helps to reduce context switching. Of course, the actual threads are handled by .net, so may be migrated across cores.

Within a given priority level, the tasks will be run in FIFO order. 

Tasks cannot be *async* ones -- they need to be normal synchronous tasks.

Once a thread has exhausted all tasks at its given priority level, it will do the first of these that it can: 

1. Steal a task from another thread, so we finish all tasks at the priority level faster
2. Take a task from the next (lower) priority level
3. Wait until a task is added (if no more tasks remain)

```C#
var tasks = new Task<double>[Environment.ProcessorCount * ChunksPerProcessor * JobsPerChunk];
var threads = Environment.ProcessorCount;
for (var i = 0; i < tasks.Length; ++i)
{
    // assign a preferred thread and priority
    var preferredThread = i % threads;
    var priority = i % PrioLevels;
    // schedule the task to run 
    tasks[i] = orderingScheduler.Run(priority, preferredThread, () => ExpensiveOperation());
}

await Task.WhenAll(tasks);
```

Note that the `OrderingScheduler` will continue running forever; it accepts a cancellation token in the constructor that you can use to shut it down:

```C#
cancellationTokenSource.Cancel();
orderingScheduler.WaitForShutdown();
```
