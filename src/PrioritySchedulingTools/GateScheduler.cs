using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Resources;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Schema;

namespace PrioritySchedulingTools
{
    public enum State
    {
        Wait,
        Active,
        Complete
    };

    public readonly struct PrioId : IEquatable<PrioId>, IComparable<PrioId>
    {
        readonly int Prio;
        readonly long Id;

        public PrioId(int prio, long id)
        {
            Prio = prio;
            Id = id;
        }

        public int CompareTo(PrioId other)
        {
            if (Prio < other.Prio) return -1;
            if (Prio > other.Prio) return +1;
            if (Id < other.Id) return -1;
            if (Id > other.Id) return +1;
            return 0;
        }

        public bool Equals(PrioId other)
        {
            return Prio == other.Prio & Id == other.Id;
        }

        public static bool operator >(PrioId a, PrioId b) => a.CompareTo(b) == 1;
        public static bool operator <(PrioId a, PrioId b) => a.CompareTo(b) == -1;
        public static bool operator >=(PrioId a, PrioId b) => a.CompareTo(b) >= 0;
        public static bool operator <=(PrioId a, PrioId b) => a.CompareTo(b) <= 0;
        public static bool operator ==(PrioId a, PrioId b) => a.Equals(b);
        public static bool operator !=(PrioId a, PrioId b) => !a.Equals(b);

        public override bool Equals(object obj)
        {
            if (obj is PrioId)
                return Equals((PrioId)obj);
            return false;
        }

        public override int GetHashCode() => HashCode.Combine(Prio, Id);
    }

    public class GateScheduler
    {
        public class PriorityGate
        {
            private readonly GateScheduler _scheduler;
            private readonly CancellationToken _cancellationToken;
            
            // local lock for less contention on WaitToContinueAsync calls; mostly uncontended
            private readonly object _gateLock = new object(); 

            public readonly int Prio;
            public readonly long Id;

            private State _currentState;

            private SemaphoreSlim _semaphore = null;

            internal PriorityGate(GateScheduler scheduler, int priority, long id, CancellationToken ct)
            {
                _scheduler = scheduler;
                _cancellationToken = ct;

                _currentState = State.Wait;
                Prio = priority;
                Id = id;

                _scheduler.AddGate(this);
            }

            internal void Complete()
            {
                lock (_gateLock)
                {
                    // mark complete and dispose the semaphore
                    _currentState = State.Complete;
                    _semaphore?.Dispose();
                    _semaphore = null;
                }
            }

            public Task WaitToContinueAsync()
            {
                lock (_gateLock)
                {
                    // immediately continue -- we're active
                    if (_currentState == State.Active)
                        return Task.CompletedTask;

                    // should be Wait here
                    if (_currentState != State.Wait) throw new InvalidOperationException($"{nameof(WaitToContinueAsync)} invalid state {_currentState}");

                    // create semaphore if not created yet
                    if (_semaphore == null)
                    {
                        _semaphore = new SemaphoreSlim(0, 1);
                    }
                    else
                    {
                        // existing semaphor: clear existing signalled state (will return immediately)
                        if (_semaphore.CurrentCount == 1)
                            _semaphore.Wait();
                    }

                    // return task waiting on the semaphor
                    return _semaphore.WaitAsync(_cancellationToken);
                }
            }

            internal void SetActive()
            {
                lock (_gateLock)
                {
                    if (_currentState != State.Wait) throw new InvalidOperationException($"{nameof(SetActive)} invalid transition {_currentState} -> {State.Active}");
                    _currentState = State.Active;

                    // release semaphore if it exists -- i.e. allow the waiting task to proceed.
                    // note that we check it isn't already signalled, from active->wait->active before the gate hit ConditionalHalt()
                    if (_semaphore != null && _semaphore.CurrentCount == 0)
                        _semaphore.Release();
                }
            }

            internal void SetWait()
            {
                lock (_gateLock)
                {
                    if (_currentState != State.Active) throw new InvalidOperationException($"{nameof(SetWait)} invalid transition {_currentState} -> {State.Wait}");
                    _currentState = State.Wait;
                }
            }

            public override string ToString() => $"{nameof(PriorityGate)}[id {Id} prio {Prio} state {_currentState} semaphore {_semaphore?.CurrentCount}]";

            public PrioId PrioId { get => new PrioId(Prio, Id); }

            public State GetCurrentState()
            {
                lock (_gateLock)
                {
                    return _currentState;
                }
            }
        }

        public const int InitialQueueSize = 2048;

        // invariants
        readonly object _schedulerLock = new object();
        readonly int _concurrency;

        // using a sorted list for priority; we'll mostly be reading from this; 
        // gates are in a Queue, which is a circular buffer, and very fast for linear read with an iterator
        readonly SortedList<int, Queue<PriorityGate>> _gates = new SortedList<int, Queue<PriorityGate>>();
        long _idCounter = 0;

        // keep track of which gates are currently active
        readonly Dictionary<PrioId, PriorityGate> _activeGates = new Dictionary<PrioId, PriorityGate>();

        public GateScheduler(int concurrency)
        {
            _concurrency = concurrency;
        }

        private void CompletedGate(PriorityGate priorityGate)
        {
            lock (_schedulerLock)
            {
                Debug.Assert(priorityGate.GetCurrentState() != State.Complete, "cannot complete more than once");

                // Gate can be removed even if it is inactive -- this will happen if a cancellation
                // token has been cancelled before the task even starts. 
                _activeGates.Remove(priorityGate.PrioId);
                priorityGate.Complete();

                // start highest-priority gate that's waiting
                if (_activeGates.Count < _concurrency)
                {
                    var nextGate = FindNextWaitingGate();
                    if (nextGate != null)
                    {
                        GateTransitionActive(nextGate);
                    }
                }
            }
        }



        private void AddGate(PriorityGate priorityGate)
        {
            lock (_schedulerLock)
            {
                // add priority stratum as required
                if (!_gates.TryGetValue(priorityGate.Prio, out var gateQueue))
                {
                    _gates[priorityGate.Prio] = gateQueue = new Queue<PriorityGate>(InitialQueueSize);
                }
                // add the gate to the end of the queue
                gateQueue.Enqueue(priorityGate);

#if DIAGNOSTICS
                Console.Error.WriteLine($"Gate added: {priorityGate}");
#endif

                if (_activeGates.Count < _concurrency)
                {
                    // set active immediately if we're below the concurrency limit
                    GateTransitionActive(priorityGate);
#if DIAGNOSTICS
                    Console.Error.WriteLine($"Immediately set active: {priorityGate}");
#endif
                }
                else
                {
                    // otherwise, check to see if this gate takes priority over the least important active one
                    var leastImportantActive = FindLeastImportantActiveGate();
                    if (leastImportantActive != null && priorityGate.PrioId < leastImportantActive.PrioId)
                    {
                        SwapActive(leastImportantActive, priorityGate);
                    }
                }

#if DIAGNOSTICS
                Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)));
#endif
            }
        }



        private PriorityGate FindNextWaitingGate()
        {
            foreach (var queue in _gates.Values)
            {
                var gq = queue;

                // remove all completed gates; fewer to search through later, and we free them for GC
                while (gq.TryPeek(out var g) && g.GetCurrentState() == State.Complete)
                {
                    gq.Dequeue();
                }

                // return first waiting or unstarted gate
                foreach (var gate in gq)
                {
                    if (gate.GetCurrentState() == State.Wait)
                    {
                        // return the gate we found
                        return gate;
                    }
                }
            }
            return null;
        }

        // inside lock
        private PriorityGate FindLeastImportantActiveGate()
        {
            if (_activeGates.Count == 0) return null;
            return _activeGates[_activeGates.Keys.Max()];
        }

        // inside lock
        private void SwapActive(PriorityGate leastImportantActive, PriorityGate priorityGate)
        {
#if DIAGNOSTICS
            Console.Error.WriteLine($"Swapping active from {leastImportantActive} -> {priorityGate}");
#endif
            GateTransitionWait(leastImportantActive);
            GateTransitionActive(priorityGate);
        }

        // inside lock
        private void GateTransitionActive(PriorityGate gate)
        {
            gate.SetActive();
            _activeGates.Add(gate.PrioId, gate);
        }

        // inside lock
        private void GateTransitionWait(PriorityGate gate)
        {
            gate.SetWait();
            _activeGates.Remove(gate.PrioId);
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
        public int GetTotalQueueSize()
        {
            lock (_schedulerLock)
            {
                return _gates.Values.Sum(gq => gq.Count);
            }
        }

    }
}
