using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrioritySchedulingTools
{
    public class GateScheduler
    {
        public enum State
        {
            BeforeStart,
            Waiting,
            Active,
            Complete
        };

        public class PriorityGate
        {
            private readonly GateScheduler _scheduler;
            private readonly CancellationToken _cancellationToken;

            public readonly int Prio;
            public readonly long Id;
            public State CurrentState { get; private set; }

            private SemaphoreSlim _semaphore = new SemaphoreSlim(0, 1);

            internal PriorityGate(GateScheduler scheduler, int priority, long id, CancellationToken ct)
            {
                _scheduler = scheduler;
                _cancellationToken = ct;

                CurrentState = State.BeforeStart;
                Prio = priority;
                Id = id;

                _scheduler.AddGate(this);
            }

            internal void MarkComplete()
            {
                CurrentState = State.Complete;
            }

            public Task WaitToContinueAsync()
            {
                return _scheduler.WaitToContinueAsync(this);
            }

            internal void MarkActive()
            {
                CurrentState = State.Active;
            }

            internal void Proceed()
            {
                CurrentState = State.Active;
                _semaphore.Release();
            }

            internal Task Halt()
            {
                CurrentState = State.Waiting;
                return _semaphore.WaitAsync(_cancellationToken);
            }

            public override string ToString() => $"{nameof(PriorityGate)}[id {Id} prio {Prio} waiting {CurrentState}]";
        }

        internal class GateList
        {
            private const int TrimListSize = 1024;
            private const int InitialListSize = 2048;

            internal int StartIndex;
            internal List<PriorityGate> Gates = new List<PriorityGate>(InitialListSize);

            // conditionally remove completed items when we have enough of them; 
            // it's very inefficient removing items from the start of a list one by one, 
            // as it involves a large copy back of the rest of the array.
            internal void CheckTrimList()
            {
                if (StartIndex > TrimListSize)
                {
                    Gates.RemoveRange(0, StartIndex);
                    StartIndex = 0;
                }
            }
        }

        // invariants
        readonly object _lk = new object();
        readonly int _concurrency;

        // using a sorted list for priority; we'll mostly be reading from this.
        readonly SortedList<int, GateList> _gates = new SortedList<int, GateList>();
        long _idCounter = 0;
        int _currentActive;

        bool _refreshNextWaitingGate = false;
        PriorityGate _nextWaitingGate = null;

        public GateScheduler(int concurrency)
        {
            _concurrency = concurrency;
        }

        private void RemoveGate(PriorityGate priorityGate)
        {
            lock (_lk)
            {
                // mark dirty so we do search on next iteration
                _refreshNextWaitingGate = true;

                // Gate can be removed even if it is inactive -- this will happen if a cancellation
                // token has been cancelled before the task even starts. 
                // Only decrease active number if the task was in fact run -- gate will be Completed
                if (priorityGate.CurrentState == State.Complete)
                {
                    --_currentActive;
                }

                if (priorityGate.CurrentState == State.Active)
                {
                    throw new InvalidOperationException("Should not be in this state");
                }

                // start highest-priority gate that's waiting
                if (_currentActive < _concurrency)
                {
                    var nextGate = FindNextWaitingGate();
                    if (nextGate != null)
                    {
                        nextGate.Proceed();
                        ++_currentActive;
                    }
                }
            }
        }

        private void AddGate(PriorityGate priorityGate)
        {
            lock (_lk)
            {
                // mark dirty so we do search on next iteration
                _refreshNextWaitingGate = true;

                // add priority stratum as required
                if (!_gates.TryGetValue(priorityGate.Prio, out var gateList))
                {
                    _gates[priorityGate.Prio] = gateList = new GateList();
                }
                // add the gate to the end of the list
                gateList.Gates.Add(priorityGate);

#if DEBUG
                Debug.Assert(_currentActive == _gates.Values.Sum(l => l.Gates.Count(g => g.CurrentState == State.Active)));
                Console.WriteLine($"Gate added: {priorityGate}");
#endif
            }
        }

        private PriorityGate FindNextWaitingGate()
        {
            foreach (var list in _gates.Values)
            {
                var gl = list.Gates;
                int? newStartIndex = null;
                for (var i = list.StartIndex; i < gl.Count; ++i)
                {
                    var gate = gl[i];

                    // first incomplete item -- this will be our new search starting position
                    if (!newStartIndex.HasValue && gate.CurrentState != State.Complete)
                    {
                        newStartIndex = i;
                    }

                    // return first waiting gate
                    if (gate.CurrentState == State.Waiting)
                    {
                        // update start index and trim list if required
                        if (newStartIndex.HasValue)
                        {
                            list.StartIndex = newStartIndex.Value;
                            list.CheckTrimList();
                        }

                        // return the gate we found
                        return gate;
                    }
                }
            }
            return null;
        }

        private Task WaitToContinueAsync(PriorityGate priorityGate)
        {
            lock (_lk)
            {
                // allow up to max concurrency
                if (_currentActive < _concurrency)
                {
                    if (priorityGate.CurrentState == State.BeforeStart)
                    {
                        ++_currentActive;
                        priorityGate.MarkActive();
                    }
#if DEBUG
                    Debug.Assert(_currentActive == _gates.Values.Sum(l => l.Gates.Count(g => g.CurrentState == State.Active)));
#endif
                    return Task.CompletedTask;
                }

                // first gate entry, and we're at max concurrency -> transition to waiting                
                if (priorityGate.CurrentState == State.BeforeStart)
                {
                    return priorityGate.Halt();
                }

                // if gate is already waiting at this point, continue waiting
                if (priorityGate.CurrentState == State.Waiting)
                {
                    Debug.Fail("Should not be here.");
                    return priorityGate.Halt();
                }

                // only search for more prioritised gate if the list is dirty
                if (_refreshNextWaitingGate)
                {
                    _nextWaitingGate = FindNextWaitingGate();
                    _refreshNextWaitingGate = false;

#if DEBUG
                    Console.WriteLine($"Next waiting gate: {_nextWaitingGate}");
#endif
                }

                // hand over to the other gate if it takes priority over this one
                if (_nextWaitingGate != null)
                {
                    var shouldHandOver = _nextWaitingGate.Prio < priorityGate.Prio |
                        (_nextWaitingGate.Prio == priorityGate.Prio & _nextWaitingGate.Id < priorityGate.Id);

                    if (shouldHandOver)
                    {
#if DEBUG
                        Console.WriteLine($"Pre-empting {priorityGate} -> {_nextWaitingGate}");
#endif
                        _refreshNextWaitingGate = true; 
                        _nextWaitingGate.Proceed();
                        return priorityGate.Halt();
                    }
                }
            }

            // no other gates pre-empted this one -- proceed directly.
            return Task.CompletedTask;
        }

        // create a new gate with next id
        private PriorityGate CreateGate(int priority, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _idCounter);
            return new PriorityGate(this, priority, id, ct);
        }

        public async Task<T> GatedRun<T>(int priority, Func<PriorityGate, Task<T>> asyncFunction, CancellationToken ct = default)
        {
            // create gate first (for priorisation), then asynchronously run the function, passing the gate to it
            var gate = CreateGate(priority, ct);
            try
            {
                var task = Task.Run(async () =>
                {
                    // wait until we can start
                    await gate.WaitToContinueAsync();
                    return await asyncFunction(gate);
                }, ct);

                await task;
                return task.Result;
            }
            finally
            {
                gate.MarkComplete();
                RemoveGate(gate);
            }
        }

        public async Task GatedRun(int priority, Func<PriorityGate, Task> asyncFunction, CancellationToken ct = default)
        {
            // create gate first (for priorisation), then asynchronously run the function, passing the gate to it
            var gate = CreateGate(priority, ct);
            try
            {
                var task = Task.Run(async () =>
                {
                    // wait until we can start
                    await gate.WaitToContinueAsync();
                    await asyncFunction(gate);
                }, ct);

                await task;
            }
            finally
            {
                gate.MarkComplete();
                RemoveGate(gate);
            }
        }
    }
}
