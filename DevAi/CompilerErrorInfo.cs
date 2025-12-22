using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DevAi
{
    internal class CompilerErrorInfo
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public int Row { get; set; }
        public string Message { get; set; } = "";
    }
}
