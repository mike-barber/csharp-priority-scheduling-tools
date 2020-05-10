using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

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

            public readonly int Prio;
            public readonly long Id;
            public State CurrentState { get; private set; }

            private SemaphoreSlim _semaphore = null;

            internal PriorityGate(GateScheduler scheduler, int priority, long id, CancellationToken ct)
            {
                _scheduler = scheduler;
                _cancellationToken = ct;

                CurrentState = State.Wait;
                Prio = priority;
                Id = id;

                _scheduler.AddGate(this);
            }

            private SemaphoreSlim GetSemaphore()
            {
                if (_semaphore == null)
                    _semaphore = new SemaphoreSlim(0, 1);

                return _semaphore;
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

            internal void SetActive()
            {
                if (CurrentState != State.Wait) throw new InvalidOperationException($"{nameof(SetActive)} invalid transition {CurrentState} -> {State.Active}");
                CurrentState = State.Active;

                // release semaphore if it exists -- i.e. allow the waiting task
                // to proceed
                if (_semaphore != null)
                    _semaphore.Release();
            }

            internal void SetWait()
            {
                if (CurrentState != State.Active) throw new InvalidOperationException($"{nameof(SetWait)} invalid transition {CurrentState} -> {State.Wait}");
                CurrentState = State.Wait;
            }

            internal Task ConditionalHalt()
            {
                // immediately continue -- we're active
                if (CurrentState == State.Active)
                    return Task.CompletedTask;

                // should be Wait here
                if (CurrentState != State.Wait) throw new InvalidOperationException($"{nameof(ConditionalHalt)} invalid state {CurrentState}");

                // create semaphore if not created yet
                if (_semaphore == null)
                    _semaphore = new SemaphoreSlim(0, 1);

                return _semaphore.WaitAsync(_cancellationToken);
            }

            public override string ToString() => $"{nameof(PriorityGate)}[id {Id} prio {Prio} state {CurrentState} semaphore {_semaphore?.CurrentCount}]";

            public PrioId PrioId { get => new PrioId(Prio, Id); }
        }

        public const int InitialQueueSize = 2048;

        // invariants
        readonly object _lk = new object();
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
            lock (_lk)
            {
#if DIAGNOSTICS
                Console.WriteLine($"{nameof(CompletedGate)} lock taken thread {Thread.CurrentThread.ManagedThreadId}");
#endif


                Debug.Assert(priorityGate.CurrentState != State.Complete, "cannot complete more than once");

#if DIAGNOSTICS
                Console.WriteLine($"Gate completing: {priorityGate}, thread {Thread.CurrentThread.ManagedThreadId}");
                Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)), "CompletedGate / pre -- active count mismatch");
#endif
                // Gate can be removed even if it is inactive -- this will happen if a cancellation
                // token has been cancelled before the task even starts. 
                _activeGates.Remove(priorityGate.PrioId);
                priorityGate.Complete();

#if DIAGNOSTICS
                Console.WriteLine($"Gate completed: {priorityGate}, ACTIVE GATES: {DiagActiveGates()}");
                Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)), "CompletedGate / completed -- active count mismatch");
#endif


                // activate highest-priority waiting gate
                var activated = ConditionallyActivateWaitingGate();

#if DIAGNOSTICS
                if (activated != null) Console.WriteLine($"Gate activated: {activated}, thread {Thread.CurrentThread.ManagedThreadId}");
                Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)), "CompletedGate / activated -- active count mismatch");
#endif

#if DIAGNOSTICS
                Console.WriteLine($"{nameof(CompletedGate)} lock release...");
#endif
            }
        }

        

        private void AddGate(PriorityGate priorityGate)
        {
            lock (_lk)
            {
#if DIAGNOSTICS
                Console.WriteLine($"{nameof(AddGate)} lock taken thread {Thread.CurrentThread.ManagedThreadId}");
#endif


                // add priority stratum as required
                if (!_gates.TryGetValue(priorityGate.Prio, out var gateQueue))
                {
                    _gates[priorityGate.Prio] = gateQueue = new Queue<PriorityGate>(InitialQueueSize);
                }
                // add the gate to the end of the queue
                gateQueue.Enqueue(priorityGate);

#if DIAGNOSTICS
                Console.WriteLine($"Gate added: {priorityGate}, thread {Thread.CurrentThread.ManagedThreadId}");
                Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)), "AddGate / pre-activation -- active count mismatch");
#endif

                // activate highest-priority waiting gate (which could be this one, or another queued one with priority)
                var activated = ConditionallyActivateWaitingGate();

                // if we haven't activated this specific gate in the above call, check to see if this gate takes priority over the least important active one
                if (activated != priorityGate)
                {
                    // otherwise, check to see if this gate takes priority over the least important active one
                    var leastImportantActive = FindLeastImportantActiveGate();
                    if (leastImportantActive != null && priorityGate.PrioId < leastImportantActive.PrioId)
                    {
                        SwapActive(leastImportantActive, priorityGate);
                    }
#if DIAGNOSTICS
                    Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)), "AddGate / SwapActive -- active count mismatch");
#endif
                }

#if DIAGNOSTICS
                Console.WriteLine($"{nameof(AddGate)} lock release...");
#endif
            }
        }

        /// <summary>
        /// inside lock: start highest-priority gate that's waiting, if we have concurrency available, 
        /// </summary>
        /// <returns>the gate that was activated, or null if it was not possible to activate one now</returns>
        private PriorityGate ConditionallyActivateWaitingGate()
        {
            if (_activeGates.Count < _concurrency)
            {
                var nextGate = FindNextWaitingGate();
                if (nextGate != null)
                {
                    GateTransitionActive(nextGate);
                    return nextGate;
                }
            }
            return null;
        }


        // inside lock
        private PriorityGate FindNextWaitingGate()
        {
            foreach (var queue in _gates.Values)
            {
                var gq = queue;

                // remove all completed gates; fewer to search through later, and we free them for GC
                while (gq.TryPeek(out var g) && g.CurrentState == State.Complete)
                {
                    gq.Dequeue();
                }

                // return first waiting or unstarted gate
                foreach (var gate in gq)
                {
                    if (gate.CurrentState == State.Wait)
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
            Console.WriteLine($"Swapping active from {leastImportantActive} -> {priorityGate}");
            Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)), "SwapActive / pre -- active count mismatch");
#endif
            GateTransitionWait(leastImportantActive);

#if DIAGNOSTICS
            Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)), "SwapActive / transitioned wait -- active count mismatch");
#endif

            GateTransitionActive(priorityGate);

#if DIAGNOSTICS
            Debug.Assert(_activeGates.Count() == _gates.Values.Sum(l => l.Count(g => g.CurrentState == State.Active)), "SwapActive / transitioned active -- active count mismatch");
#endif
        }

        // inside lock
        private void GateTransitionActive(PriorityGate gate)
        {
#if DIAGNOSTICS
            Console.WriteLine($"Tranitioning to Active: {gate}");
#endif
            Debug.Assert(gate.CurrentState == State.Wait, "gate must be waiting to transition to active");
            gate.SetActive();
            _activeGates.Add(gate.PrioId, gate);

#if DIAGNOSTICS
            Console.WriteLine($"Tranitioned to Active: {gate}, ACTIVE GATES: {DiagActiveGates()}");
#endif
        }

        

        // inside lock
        private void GateTransitionWait(PriorityGate gate)
        {
#if DIAGNOSTICS
            Console.WriteLine($"Tranitioning to Wait: {gate}");
#endif

            Debug.Assert(gate.CurrentState == State.Active, "gate must be active to transition to wait");
            gate.SetWait();
            _activeGates.Remove(gate.PrioId);

#if DIAGNOSTICS
            Console.WriteLine($"Tranitioned to Wait: {gate}, ACTIVE GATES: {DiagActiveGates()}");
#endif
        }

        private string DiagActiveGates() => string.Format($"[ {string.Join(",", _activeGates.Values)} ]");

        private Task WaitToContinueAsync(PriorityGate priorityGate)
        {
            lock (_lk)
            {
#if DIAGNOSTICS
                Console.WriteLine($"{nameof(WaitToContinueAsync)} lock taken thread {Thread.CurrentThread.ManagedThreadId}");
#endif
                var wait = priorityGate.ConditionalHalt();

#if DIAGNOSTICS
                Console.WriteLine($"{nameof(WaitToContinueAsync)} lock release...");
#endif
                return wait;
            }
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
    }
}
