using PrioritySchedulingTools.Tests;
using System;

namespace PrioritySchedulingTools.RunTest
{
    class Program
    {
        static void Main(string[] args)
        {
            // easier to diagnose issues with normal console output
            var test = new GateExceptionAndCancellation();
            test.ExceptionTestAsyncAll();
        }
    }
}
