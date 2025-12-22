using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DevAi
{
    internal class RunResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = "";
        public string Errors { get; set; } = "";
        public int ExitCode { get; set; }

        public Process RunningProcess { get; set; }

        // Completes when the running process has finished and Output/Errors/ExitCode have been populated
        public Task Completion { get; set; }
    }
}
