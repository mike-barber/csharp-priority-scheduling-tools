# C# Job priority scheduling tools

This is a library to assist with scheduling of compute-bound `Task`s. It aims to offer the following characteristics:

* Performant -- it's not a useful tool for compute-bound tasks if it's expensive!
* Simplicity
    * Simplicity of implementation -- to permit practical reviewing and testing
    * Simplicity of use -- to permit consumers to use it without danger or difficulty
* Tested -- at least on a basic level initially

Given that the problem is parallelism, the implementation is (unavoidably) somewhat complex, but hopefully not needlessly so.

This library provides two tools: 

* `GateScheduler` -- for managing longer *interruptible* tasks with priorities; this is probably the more interesting of the two, given that it's a somewhat more novel idea.
* `OrderingScheduler` -- for managing shorter *uninterruptible* tasks with priorities, with a degree of thread affinity.

## What it is *not* 

### Not a `TaskScheduler` replacement

This is *not* an attempt to replace the `TaskScheduler` with a more specific one that handles task prioritisation. That has been done before, and is quite complex. The primary example of this would be the now-unmaintained [Microsoft Parallel Extensions Extras](https://devblogs.microsoft.com/pfxteam/a-tour-of-parallelextensionsextras/), which provided several alternative `TaskScheduler`s. Only one of these has really made it into the core framework, and this one is the [ConcurrentExclusiveSchedulerPair](https://docs.microsoft.com/en-us/dotnet/api/system.threading.tasks.concurrentexclusiveschedulerpair?view=netcore-3.1). This implementation also allows you to specify maximum concurrency. 

An idea of the complexities involved can be gained by reviewing the [source code](https://github.com/dotnet/runtime/blob/master/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/ConcurrentExclusiveSchedulerPair.cs). It's also worth noting that it relies on calling certain `internal` methods. I've attempted some of this previously, but never been happy with the results. There are a lot of edge cases.

Instead, the idea is to provide tools that work on top of the normal TaskScheduler instead of attempting to replace it.

### Not for *concurrency*: It's for compute *parallelism*

These tools are specifically designed for compute-bound workloads, especially given the explicit concurrency limits they implement. Do not use them for IO-bound workloads -- you'll cause an artificial bottleneck. 

I wanted to create a library that is simple enough to review, test and use. Adding the ability to deal with asynchronous IO (i.e. concurrency) would add a great deal of complexity, and may not be practical. For instance, when limiting concurrency to a specific number of tasks, it becomes a lot more complicated if some of those tasks actually aren't doing anything while waiting for an IO completion:

* Do you regard them as being "active" and keep them in your count of active tasks? 
* Or do you regard them as being "inactive" from a compute point of view, and let other compute task proceed instead?
* What do you do when the IO completion occurs?

## Priorities

Priorities are supplied as integers, with **lower numbers having priority** over higher numbers. So for a three-class problem, you'd could have:

0. High Priority
1. Medium Priority
2. Low Priority

You're free to use these any way you'd like, including negative numbers. However, a new queue is created for each new class, and is included in the scan for the most prioritised item, so having more than a few will lead to performance issues.

## `GateScheduler`

The `GateScheduler` allows you to run up to `N` concurrent tasks with as many priorities as required. 

Rather than handling the actual scheduling of tasks directly, the idea is that tasks are *interruptible*. The scheduler will give you a `gate`, and you call `await gate.WaitToContinueAsync();` inside your task. If this task can proceed immediately, it does. If another more important gate is waiting, you'll be delayed until it is complete, and the more important task will be allowed to proceed instead.

Tasks can run for some time, and you can insert several gate checks at various points as required. They're not free, given that the scheduler checks whether the gate can proceed; this requires some synchronisation. Currently, it's a local lock, and will be mostly uncontended. These are quite fast, but definitely not free.

You want to aim for some degree of chunkiness in the work you're doing between checks. The more often you check the gate, the earlier the task can be interrupted by a more important one; the cost of doing this is increased time wasted on synchronisation. Tune it for your needs :)


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

Tasks cannot be *async* ones -- they to be normal synchronous tasks. Supporting *async* may not make sense and will probably add quite a bit of complexity.

Once a thread has exhausted all tasks at its given priority level, it will do the first of these that it can: 

1. Steal a task from another thread, so we finish all tasks at the priority level faster
2. Take a task from the next priority level
3. Wait, asynchronously, until a task is added (if no more tasks remain)

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

## `DIAGNOSTICS`

Define this Conditional Compilation Symbol to enable tracing output to the console; you definitely don't want this in a Release build, but it helps to understand what the tools are doing.