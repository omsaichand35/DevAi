using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DevAi
{
    internal class PythonRunner : ICodeRunner
    {
        public string Language => "Python";

        public bool CanRun(string fileExtension)
            => fileExtension.Equals(".py", StringComparison.OrdinalIgnoreCase);

        public RunResult Run(string code, string workingDirectory)
        {
            string dir = workingDirectory;
            try
            {
                if (string.IsNullOrWhiteSpace(dir))
                    dir = Path.GetTempPath();
                Directory.CreateDirectory(dir);
            }
            catch
            {
                dir = Path.GetTempPath();
            }

            string tempFile = Path.Combine(dir, $"devai_{Guid.NewGuid():N}.py");
            File.WriteAllText(tempFile, code ?? string.Empty);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{tempFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir
            };

            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();

            var result = new RunResult();

            try
            {
                using (Process process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        result.Success = false;
                        result.Errors = "Failed to start python process";
                        result.ExitCode = -1;
                        return result;
                    }

                    output.Append(process.StandardOutput.ReadToEnd());
                    error.Append(process.StandardError.ReadToEnd());
                    process.WaitForExit();

                    result.Success = process.ExitCode == 0;
                    result.Output = output.ToString();
                    result.Errors = error.ToString();
                    result.ExitCode = process.ExitCode;
                    result.RunningProcess = null;
                    result.Completion = Task.CompletedTask;

                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors = ex.Message;
                result.ExitCode = -1;
                return result;
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
