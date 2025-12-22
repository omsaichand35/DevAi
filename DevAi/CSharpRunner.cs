using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace DevAi
{
    internal class CSharpRunner : ICodeRunner
    {
        public string Language => "C#";

        public bool CanRun(string fileExtension)
        {
            return fileExtension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
        }

        public RunResult Run(string code, string workingDirectory)
        {
            var result = new RunResult();

            try
            {
                // Use a more permanent location that can be whitelisted in antivirus
                // Default to user's Documents folder if workingDirectory is in Temp
                if (workingDirectory.Contains("Temp") || workingDirectory.Contains("TEMP"))
                {
                    string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    workingDirectory = Path.Combine(documentsPath, "DevAiRuns");
                }

                Directory.CreateDirectory(workingDirectory);

                // Use unique file names to avoid file locking issues
                string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
                string sourceFile = Path.Combine(workingDirectory, $"Program_{uniqueId}.cs");
                string exeFile = Path.Combine(workingDirectory, $"Program_{uniqueId}.exe");

                File.WriteAllText(sourceFile, code);

                // Use the latest .NET Framework compiler (v4.7.2 compatible)
                string cscPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"
                );

                // Compile using csc with proper compiler options
                ProcessStartInfo compileInfo = new ProcessStartInfo
                {
                    FileName = cscPath,
                    Arguments = $"/nologo /optimize+ /target:exe /out:\"{exeFile}\" \"{sourceFile}\"",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process compile = Process.Start(compileInfo))
                {
                    string compileOut = compile.StandardOutput.ReadToEnd();
                    string compileErr = compile.StandardError.ReadToEnd();
                    compile.WaitForExit();

                    if (compile.ExitCode != 0)
                    {
                        result.Success = false;
                        result.Errors = compileOut + compileErr;
                        
                        // Clean up source file on compile error
                        try { File.Delete(sourceFile); } catch { }
                        
                        return result;
                    }
                }

                // Small delay to allow antivirus to scan the file
                System.Threading.Thread.Sleep(500);

                // Run the exe
                ProcessStartInfo runInfo = new ProcessStartInfo
                {
                    FileName = exeFile,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                try
                {
                    using (Process run = Process.Start(runInfo))
                    {
                        if (run == null)
                        {
                            result.Success = false;
                            result.Errors = "Failed to start process.";
                        }
                        else
                        {
                            // Read output synchronously so callers receive full output when Run returns
                            result.RunningProcess = run;
                            result.Output = run.StandardOutput.ReadToEnd();
                            result.Errors = run.StandardError.ReadToEnd();
                            run.WaitForExit();
                            result.ExitCode = run.ExitCode;
                            result.Success = run.ExitCode == 0;
                        }
                    }

                    // Clean up files after execution
                    try
                    {
                        // Small delay before cleanup to ensure process has fully released files
                        System.Threading.Thread.Sleep(100);
                        File.Delete(sourceFile);
                        File.Delete(exeFile);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == -2147467259)
                {
                    result.Success = false;
                    result.Errors = "⚠️ ANTIVIRUS BLOCKING EXECUTION\n\n" +
                        "Your antivirus (McAfee) is blocking the compiled program from running.\n\n" +
                        "To fix this:\n" +
                        "1. Open McAfee Security Center\n" +
                        "2. Go to 'Real-Time Scanning' settings\n" +
                        "3. Add this folder to exclusions: " + workingDirectory + "\n" +
                        "   OR add exclusion for: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DevAiRuns") + "\n\n" +
                        "Original error: " + ex.Message;

                    try { File.Delete(sourceFile); } catch { }
                    try { File.Delete(exeFile); } catch { }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result.Success = false;
                    result.Errors = "⚠️ ACCESS DENIED\n\n" +
                        "Cannot access the working directory or files.\n\n" +
                        "Try running Visual Studio as Administrator or add an antivirus exception.\n\n" +
                        "Original error: " + ex.Message;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors = "❌ UNEXPECTED ERROR\n\n" + ex.ToString();
            }

            return result;
        }
    }
}
