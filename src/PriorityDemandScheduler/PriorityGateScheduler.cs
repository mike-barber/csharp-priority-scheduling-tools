using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PriorityDemandScheduler
{
    public class PriorityGateScheduler
    {
        public class PriorityGate : IDisposable
        {
            PriorityGateScheduler _scheduler;
            public readonly int Prio;
            public readonly long Id;
            public bool Waiting { get; private set; }

            private SemaphoreSlim _semaphore = new SemaphoreSlim(0, 1);

            internal PriorityGate(PriorityGateScheduler scheduler, int priority, long id)
            {
                _scheduler = scheduler;
                Waiting = true;
                Prio = priority;
                Id = id;

                _scheduler.AddGate(this);
            }

            public void Dispose()
            {
                _scheduler.RemoveGate(this);
                _semaphore.Dispose();
            }

            public Task PermitYield()
            {
                return _scheduler.PermitYield(this);
            }

            internal void MarkActive()
            {
                Waiting = false;
            }

            internal void Proceed()
            {
                Waiting = false;
                _semaphore.Release();
            }

            internal Task Halt()
            {
                Waiting = true;
                return _semaphore.WaitAsync();
            }

            public override string ToString() => $"{nameof(PriorityGate)}[id {Id} prio {Prio} waiting {Waiting}]";
        }

        // invariants
        readonly object _lk = new object();
        readonly int _concurrency;

        // state
        readonly SortedList<int, SortedList<long, PriorityGate>> _gates = new SortedList<int, SortedList<long, PriorityGate>>();
        long _idCounter = 0;
        int _currentActive;


        public PriorityGateScheduler(int concurrency)
        {
            _concurrency = concurrency;
        }

        private void RemoveGate(PriorityGate priorityGate)
        {
            lock (_lk)
            {
                _gates[priorityGate.Prio].Remove(priorityGate.Id);

                // Gate can only be removed if it was active -- otherwise it won't proceed 
                Debug.Assert(!priorityGate.Waiting);
                --_currentActive;

                Console.WriteLine($"Gate removed: {priorityGate}");
                Debug.Assert(_currentActive == _gates.Values.Sum(l => l.Values.Sum(g => g.Waiting ? 0 : 1)));
                
                // start a gate that's waiting
                if (_currentActive < _concurrency)
                {
                    foreach (var (prio,list) in _gates)
                    {
                        foreach (var (id, gate) in list)
                        {
                            if (gate.Waiting)
                            {
                                gate.Proceed();
                                ++_currentActive;
                                return;   
                            }
                        }
                    }
                }
            }
        }

        private void AddGate(PriorityGate priorityGate)
        {
            lock (_lk)
            {
                if (!_gates.TryGetValue(priorityGate.Prio, out var gateList))
                {
                    _gates[priorityGate.Prio] = gateList = new SortedList<long, PriorityGate>();
                }
                gateList[priorityGate.Id] = priorityGate;

                Debug.Assert(_currentActive == _gates.Values.Sum(l => l.Values.Sum(g => g.Waiting ? 0 : 1)));

                Console.WriteLine($"Gate added: {priorityGate}");
            }
        }

        private Task PermitYield(PriorityGate priorityGate)
        {
            lock (_lk)
            {
                // allow up to max concurrency
                if (_currentActive < _concurrency)
                {
                    if (priorityGate.Waiting)
                    {
                        ++_currentActive;
                        priorityGate.MarkActive();
                    }
                    Debug.Assert(_currentActive == _gates.Values.Sum(l => l.Values.Sum(g => g.Waiting ? 0 : 1)));
                    return Task.CompletedTask;
                }

                // if gate is already waiting at this point, continue waiting
                if (priorityGate.Waiting)
                {
                    return priorityGate.Halt();
                }

                // find earlier, more prioritised gate
                foreach (var (prio, list) in _gates)
                {
                    if (prio > priorityGate.Prio)
                        break;

                    foreach (var (id, gate) in list)
                    {
                        if (id >= priorityGate.Id & gate.Prio == priorityGate.Prio)
                            break;

                        // yield to this task
                        if (gate.Waiting)
                        {
                            Console.WriteLine($"Pre-empting {priorityGate} -> {gate}");
                            gate.Proceed();
                            return priorityGate.Halt();
                        }
                    }
                }
            }

            return Task.CompletedTask;
        }

        public PriorityGate CreateGate(int priority)
        {
            var id = Interlocked.Increment(ref _idCounter);
            return new PriorityGate(this, priority, id);
        }

        public async Task<T> GatedRun<T>(int priority, Func<PriorityGate, Task<T>> asyncFunction)
        {
            // create gate first (for priorisation), then asynchronously run the function, passing the gate to it
            using (var g = CreateGate(priority))
            {
                // capture gate
                var gate = g;

                var task = Task.Run(async () =>
                {
                    // wait until we can start
                    await gate.PermitYield();
                    return await asyncFunction(gate);
                });

                await task;
                return task.Result;
            }
        }
    }
}
