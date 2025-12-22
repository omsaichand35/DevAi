using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DevAi
{
    internal interface ICodeRunner
    {
        string Language { get; }
        bool CanRun(string fileExtension);
        RunResult Run(string code, string workingDirectory);
    }
}
