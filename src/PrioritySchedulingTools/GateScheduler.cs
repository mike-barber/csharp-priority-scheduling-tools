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

            internal void Complete()
            {
                // mark complete and dispose the semaphore
                CurrentState = State.Complete;
                _semaphore?.Dispose();
                _semaphore = null;
            }

            public Task WaitToContinueAsync()
            {
                return _scheduler.WaitToContinueAsync(this);
            }

            internal void Proceed()
            {
                switch (CurrentState)
                {
                    case State.BeforeStart:
                        CurrentState = State.Active;
                        break;
                    case State.Waiting:
                        CurrentState = State.Active;
                        _semaphore.Release();
                        break;
                    default:
                        throw new InvalidOperationException($"Cannot proceed from state {CurrentState}");
                }
            }

            internal Task Halt()
            {
                CurrentState = State.Waiting;
                return _semaphore.WaitAsync(_cancellationToken);
            }

            public override string ToString() => $"{nameof(PriorityGate)}[id {Id} prio {Prio} waiting {CurrentState}]";
        }

        public const int TrimListAtSizeCompleted = 256;
        public const int InitialListSize = 2048;

        internal class GateList
        {
            internal int StartIndex;
            internal List<PriorityGate> Gates = new List<PriorityGate>(InitialListSize);

            // conditionally remove completed items when we have enough of them; 
            // it's very inefficient removing items from the start of a list one by one, 
            // as it involves a large copy back of the rest of the array.
            internal void CheckTrimList()
            {
                if (StartIndex > TrimListAtSizeCompleted)
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

        private void CompletedGate(PriorityGate priorityGate)
        {
            lock (_lk)
            {
                // Gate can be removed even if it is inactive -- this will happen if a cancellation
                // token has been cancelled before the task even starts. 
                switch (priorityGate.CurrentState)
                {
                    case State.Active:
                        --_currentActive;
                        priorityGate.Complete();
                        break;
                    case State.BeforeStart:
                        priorityGate.Complete();
                        break;
                    default:
                        throw new InvalidOperationException($"Invalid transition {priorityGate.CurrentState} -> {State.Complete}");
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

                // mark dirty so we do search on next iteration
                _refreshNextWaitingGate = true;
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

#if DIAGNOSTICS
                Debug.Assert(_currentActive == _gates.Values.Sum(l => l.Gates.Count(g => g.CurrentState == State.Active)));
                Console.Error.WriteLine($"Gate added: {priorityGate}");
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

                    // return first waiting or unstarted gate
                    if (gate.CurrentState == State.Waiting | gate.CurrentState == State.BeforeStart)
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
                        priorityGate.Proceed();
                    }
#if DIAGNOSTICS
                    Debug.Assert(_currentActive == _gates.Values.Sum(l => l.Gates.Count(g => g.CurrentState == State.Active)));
#endif
                    return Task.CompletedTask;
                }

                // first gate entry, and we're at max concurrency -> transition to waiting                
                if (priorityGate.CurrentState == State.BeforeStart)
                {
                    return priorityGate.Halt();
                }

                // integrity assertion -- should only be Active at this point
                if (priorityGate.CurrentState != State.Active)
                {
                    throw new InvalidOperationException($"Gate should be Active at this point, but is {priorityGate.CurrentState}");
                }

                // only search for more prioritised gate if the list is dirty
                if (_refreshNextWaitingGate)
                {
                    _nextWaitingGate = FindNextWaitingGate();
                    _refreshNextWaitingGate = false;

#if DIAGNOSTICS
                    Console.Error.WriteLine($"Next waiting gate: {_nextWaitingGate}");
#endif
                }

                // hand over to the other gate if it takes priority over this one
                if (_nextWaitingGate != null)
                {
                    var shouldHandOver = _nextWaitingGate.Prio < priorityGate.Prio |
                        (_nextWaitingGate.Prio == priorityGate.Prio & _nextWaitingGate.Id < priorityGate.Id);

                    if (shouldHandOver)
                    {
#if DIAGNOSTICS
                        Console.Error.WriteLine($"Pre-empting {priorityGate} -> {_nextWaitingGate}");
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
                CompletedGate(gate);
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
                CompletedGate(gate);
            }
        }

        // EXPENSIVE: for debugging and testing
        public int GetTotalListSize()
        {
            lock (_lk)
            {
                return _gates.Values.Sum(gl => gl.Gates.Count);
            }
        }
       
    }
}
