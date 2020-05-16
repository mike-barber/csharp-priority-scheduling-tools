using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace PrioritySchedulingTools
{
    public interface IGate
    {
        Task WaitToContinueAsync();
    }
}
