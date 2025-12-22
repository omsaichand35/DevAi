using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace DevAi
{
    internal sealed class RoslynIntellisenseService : IDisposable
    {
        private readonly AdhocWorkspace workspace;
        private readonly ProjectId projectId;
        private readonly string projectName = "DevAiInMemoryProject";

        private static readonly string[] pythonKeywords = new[]
        {
            "False","None","True","and","as","assert","break","class","continue","def","del","elif","else","except","finally","for",
            "from","global","if","import","in","is","lambda","nonlocal","not","or","pass","raise","return","try","while","with","yield"
        };

        public RoslynIntellisenseService()
        {
            workspace = new AdhocWorkspace();
            projectId = ProjectId.CreateNewId(projectName);

            var refs = new List<MetadataReference>();
            // Add common runtime references
            TryAddReference(refs, typeof(object).GetTypeInfo().Assembly);
            TryAddReference(refs, typeof(Enumerable).GetTypeInfo().Assembly);
            TryAddReference(refs, typeof(Console).GetTypeInfo().Assembly);
            TryAddReference(refs, typeof(List<>).GetTypeInfo().Assembly);
            TryAddReference(refs, typeof(Uri).GetTypeInfo().Assembly);

            var projInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), projectName, projectName, LanguageNames.CSharp)
                .WithMetadataReferences(refs);

            workspace.TryApplyChanges(workspace.CurrentSolution.AddProject(projInfo));
        }

        private void TryAddReference(List<MetadataReference> refs, Assembly assembly)
        {
            try
            {
                if (assembly == null) return;
                var loc = assembly.Location;
                if (string.IsNullOrEmpty(loc)) return;
                if (File.Exists(loc))
                {
                    refs.Add(MetadataReference.CreateFromFile(loc));
                }
            }
            catch
            {
                // ignore
            }
        }

        // Default: C# completions (existing behavior)
        public Task<List<string>> GetCompletionsAsync(string source, int position)
        {
            return GetCompletionsAsync(source, position, "C#");
        }

        // Language-aware completions. For C# uses Roslyn; for Python uses Jedi if available, else simple keyword/member heuristics.
        public async Task<List<string>> GetCompletionsAsync(string source, int position, string language)
        {
            if (string.Equals(language, "Python", StringComparison.OrdinalIgnoreCase))
            {
                // Try Jedi-based completions first
                try
                {
                    var jedi = TryGetJediCompletions(source, position);
                    if (jedi != null && jedi.Count > 0)
                        return jedi;
                }
                catch
                {
                    // ignore and fallback to keyword list
                }

                // Fallback: simple keyword prefix matching
                try
                {
                    if (string.IsNullOrEmpty(source))
                        return pythonKeywords.ToList();

                    int pos = Math.Max(0, Math.Min(position, source.Length));
                    int start = pos;
                    while (start > 0 && (char.IsLetterOrDigit(source[start - 1]) || source[start - 1] == '_')) start--;
                    string prefix = source.Substring(start, pos - start);

                    if (string.IsNullOrEmpty(prefix))
                        return pythonKeywords.OrderBy(k => k).ToList();

                    var matches = pythonKeywords.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(k => k)
                        .ToList();

                    return matches;
                }
                catch
                {
                    return new List<string>();
                }
            }

            // Fallback to C# (Roslyn-based) completion
            return await GetCSharpCompletionsAsync(source, position).ConfigureAwait(false);
        }

        private List<string> TryGetJediCompletions(string source, int position)
        {
            // Require python and jedi to be available. This helper will create a temporary source file and a small helper script
            // to call jedi and print JSON list of completion names.
            string tempDir = Path.GetTempPath();
            string sourcePath = null;
            string helperPath = null;

            try
            {
                sourcePath = Path.Combine(tempDir, $"devai_src_{Guid.NewGuid():N}.py");
                File.WriteAllText(sourcePath, source ?? string.Empty);

                helperPath = Path.Combine(tempDir, "devai_jedi_tool.py");
                // Overwrite helper each time to ensure content
                string helperCode = @"import sys, json
try:
    import jedi
except Exception as e:
    print(json.dumps({'__err__': str(e)}))
    sys.exit(2)

if len(sys.argv) < 4:
    print(json.dumps({'__err__': 'args'}))
    sys.exit(2)

path = sys.argv[1]
line = int(sys.argv[2])
col = int(sys.argv[3])
try:
    with open(path, 'r', encoding='utf-8') as f:
        code = f.read()
    script = jedi.Script(code, path=path)
    comps = script.complete(line, col)
    names = [c.name for c in comps]
    print(json.dumps({'__comps__': names}))
except Exception as ex:
    print(json.dumps({'__err__': str(ex)}))
    sys.exit(2)
";
                File.WriteAllText(helperPath, helperCode);

                // compute line/col for jedi: jedi expects 1-based line and 0-based column
                int line = source == null ? 1 : (source.Substring(0, Math.Max(0, Math.Min(position, source.Length))).Count(c => c == '\n') + 1);
                int lastNl = source == null ? -1 : source.LastIndexOf('\n', Math.Max(0, Math.Min(position - 1, Math.Max(0, source.Length - 1))));
                int col = 0;
                if (source != null)
                {
                    col = position - (lastNl + 1);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = ArgumentQuote(helperPath) + " " + ArgumentQuote(sourcePath) + " " + line + " " + col,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var outSb = new System.Text.StringBuilder();
                var errSb = new System.Text.StringBuilder();

                using (var proc = new Process())
                {
                    proc.StartInfo = psi;
                    proc.EnableRaisingEvents = true;

                    proc.OutputDataReceived += (s, ea) => { if (ea.Data != null) { lock (outSb) outSb.AppendLine(ea.Data); } };
                    proc.ErrorDataReceived += (s, ea) => { if (ea.Data != null) { lock (errSb) errSb.AppendLine(ea.Data); } };

                    if (!proc.Start())
                        return null;

                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    // Wait up to 5s for process to exit; kill if it doesn't
                    bool exited = proc.WaitForExit(5000);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        proc.WaitForExit(2000);
                    }

                    string outp = outSb.ToString();
                    string err = errSb.ToString();

                    if (string.IsNullOrWhiteSpace(outp))
                    {
                        return null;
                    }

                    // look for JSON with key __comps__ or __err__ using index-based search to avoid complex escaping
                    int keyIdx = outp.IndexOf("\"__comps__\"");
                    if (keyIdx >= 0)
                    {
                        int arrStart = outp.IndexOf('[', keyIdx);
                        int arrEnd = arrStart >= 0 ? outp.IndexOf(']', arrStart) : -1;
                        if (arrStart >= 0 && arrEnd > arrStart)
                        {
                            string inner = outp.Substring(arrStart + 1, arrEnd - arrStart - 1);
                            // extract string tokens
                            var matches = Regex.Matches(inner, "\"([^\"]*)\"");
                            var list = new List<string>();
                            foreach (Match mt in matches)
                            {
                                list.Add(mt.Groups[1].Value);
                            }

                            return list.Distinct().ToList();
                        }
                    }

                    return null;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(sourcePath)) File.Delete(sourcePath); } catch { }
                // keep helper file to reduce overhead; do not delete
            }

            // local helper to quote arguments safely
            string ArgumentQuote(string s)
            {
                if (s == null) return "\"\"";
                return "\"" + s.Replace("\"", "\\\"") + "\"";
            }
        }

        private async Task<List<string>> GetCSharpCompletionsAsync(string source, int position)
        {
            try
            {
                // Add document
                var docId = DocumentId.CreateNewId(projectId, debugName: "Doc.cs");
                var text = SourceText.From(source ?? string.Empty);

                workspace.TryApplyChanges(workspace.CurrentSolution.AddDocument(docId, "Doc.cs", text));
                var document = workspace.CurrentSolution.GetDocument(docId);

                if (document == null)
                {
                    return new List<string>();
                }

                // Use reflection to find CompletionService type at runtime to avoid compile-time dependency
                Type compServiceType = null;
                try
                {
                    compServiceType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a =>
                        {
                            try { return a.GetTypes(); } catch { return new Type[0]; }
                        })
                        .FirstOrDefault(t => t.FullName == "Microsoft.CodeAnalysis.Completion.CompletionService");
                }
                catch
                {
                    compServiceType = null;
                }

                if (compServiceType == null)
                {
                    // cleanup
                    try { workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(docId)); } catch { }
                    return new List<string>();
                }

                // static GetService(Document)
                var getServiceMI = compServiceType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(Document) }, null);
                if (getServiceMI == null)
                {
                    try { workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(docId)); } catch { }
                    return new List<string>();
                }

                object compServiceInstance = null;
                try
                {
                    compServiceInstance = getServiceMI.Invoke(null, new object[] { document });
                }
                catch
                {
                    compServiceInstance = null;
                }

                if (compServiceInstance == null)
                {
                    try { workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(docId)); } catch { }
                    return new List<string>();
                }

                // Find GetCompletionsAsync instance method
                MethodInfo getCompletionsMI = null;
                try
                {
                    getCompletionsMI = compServiceType.GetMethod("GetCompletionsAsync", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                }
                catch
                {
                    getCompletionsMI = null;
                }

                if (getCompletionsMI == null)
                {
                    try { workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(docId)); } catch { }
                    return new List<string>();
                }

                // Invoke and await dynamically
                object taskObj = null;
                try
                {
                    taskObj = getCompletionsMI.Invoke(compServiceInstance, new object[] { document, position });
                }
                catch
                {
                    taskObj = null;
                }

                if (taskObj == null)
                {
                    try { workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(docId)); } catch { }
                    return new List<string>();
                }

                dynamic dynTask = taskObj;
                dynamic results = null;
                try
                {
                    results = await dynTask.ConfigureAwait(false);
                }
                catch
                {
                    try { workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(docId)); } catch { }
                    return new List<string>();
                }

                var items = new List<string>();
                if (results != null)
                {
                    try
                    {
                        var resultsItems = (IEnumerable<object>)results.Items;
                        foreach (var item in resultsItems)
                        {
                            try
                            {
                                var displayProp = item.GetType().GetProperty("DisplayText");
                                if (displayProp != null)
                                {
                                    var display = displayProp.GetValue(item) as string;
                                    if (!string.IsNullOrWhiteSpace(display) && !items.Contains(display))
                                        items.Add(display);
                                }
                            }
                            catch { }
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // cleanup document
                try { workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(docId)); } catch { }

                return items;
            }
            catch
            {
                return new List<string>();
            }
        }

        public void Dispose()
        {
            try { workspace?.Dispose(); } catch { }
        }
    }
}
