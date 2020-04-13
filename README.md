# Job prioritisation schedulers

Priorities are supplied as integers, with lower numbers having priority over higher numbers. So for a three-class problem, you'd have:

0. High Priority
1. Medium Priority
2. Low Priority

## `GatedScheduler`

The `GatedScheduler` allows you to run up to `N` concurrent tasks with as many priorities as required.

The idea here is that the tasks can run for some time, but are *interruptible*. The scheduler will give you a `gate`, and you call `await gate.WaitToContinueAsync();` inside your task. If this task can proceed immediately, it does. 

If another more important gate is waiting, you'll be delayed until it is complete, and the more important task will be allowed to proceed instead.

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
                await gate.WaitToContinueAsync();
                total += ExpensiveOperation();
            }
            return total;
        }));
    }
}
```

## `OrderingScheduler`

The `OrderingScheduler` allows up to `N` concurrent tasks, and each task is assigned a priority and a preferred thread. This works well for a large number of queued tasks that don't have much context.

The tasks will be run in priority-order, and on worker tasks that correspond to the thread, which helps to reduce context switching.

Within a given priority level, the tasks will be run in FIFO order. 

Once a thread has exhausted all tasks at its given priority level, it will do the first of these that it can: 

1. Steal a task from another thread, so we finish all tasks at the priority level faster
2. Take a task from the next (lower) priority level
3. Wait until a task is added (if no more tasks remain)

```C#
var tasks = new Task<double>[Environment.ProcessorCount * ChunksPerProcessor * JobsPerChunk];
var threads = Environment.ProcessorCount;
for (var i = 0; i < tasks.Length; ++i)
{
    var preferredThread = i % threads;
    var priority = i % PrioLevels;
    tasks[i] = orderingScheduler.Run(priority, preferredThread, () => ExpensiveOperation());
}

await Task.WhenAll(tasks);
```

Note that the `OrderingScheduler` will continue running forever; it accepts a cancellation token in the constructor that you can use to shut it down:

```C#
cancellationTokenSource.Cancel();
orderingScheduler.WaitForShutdown();
```
