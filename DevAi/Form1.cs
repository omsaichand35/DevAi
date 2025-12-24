using ScintillaNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;
using Newtonsoft.Json;

namespace DevAi
{
    public class SessionState
    {
        public string LastWorkspace { get; set; }
        public string LastFile { get; set; }
        public List<string> OpenFiles { get; set; } = new List<string>();
        public string ChatHistory { get; set; }
    }

    public partial class Form1 : Form
    {

        private bool isRunning = false;
        private int tabCounter = 1;
        private Process runningProcess;

        int inputStartIndex = 0;

        private Process cmdProcess;
        private Process collabClientProcess;
        private string currentWorkspacePath;
        private StreamWriter cmdInputWriter;
        private FileSystemWatcher chatWatcher;
        private string chatLogPath;

        private readonly string csharpKeywords =
            "abstract as base bool break byte case catch char checked class const continue " +
        "decimal default delegate do double else enum event explicit extern false finally " +
        "fixed float for foreach goto if implicit in in int interface internal is lock long " +
        "namespace new null object operator out override params private protected public " +
        "readonly ref return sbyte sealed short sizeof stackalloc static string struct " +
        "switch this throw true try typeof uint ulong unchecked unsafe ushort using " +
        "virtual void volatile while Console Math String DateTime List Dictionary";

        private readonly Dictionary<string, Type> typeAliasMap =
          new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
      {
    { "Console", typeof(Console) },
    { "Math", typeof(Math) },
    { "string", typeof(string) },
    { "DateTime", typeof(DateTime) },
    { "int", typeof(int) },
    { "double", typeof(double) },
    { "bool", typeof(bool) }
      };

        private RoslynIntellisenseService roslynService;
        private AiAgent _aiAgent;

        private List<CompilerErrorInfo> lastErrors = new List<CompilerErrorInfo>();
        // Map of error marker ID -> error index for robust click mapping
        private Dictionary<string, int> lastErrorIdMap = new Dictionary<string, int>();

        private readonly List<ICodeRunner> runners = new List<ICodeRunner>
        {
            new CSharpRunner(),
            new PythonRunner()
        };

        private System.Text.StringBuilder terminalInputBuffer = new System.Text.StringBuilder();

        private string lastTerminalDir = null;

        public Form1()
        {
            InitializeComponent();

            this.Shown += (s, e) => 
            {
                StartTerminal();
                RestoreSession();
            };

            // wire terminal keypress to capture characters reliably
            this.terminalBox.KeyPress += terminalBox_KeyPress;

            // wire bottom tab change to update terminal working directory
            try
            {
                this.bottomTabControl.SelectedIndexChanged += bottomTabControl_SelectedIndexChanged;
            }
            catch { }

            // Use single-click to jump to errors (more discoverable).
            richTextBox1.MouseClick += OutputBox_MouseClick;
            HideOutputPanel();
            CreateNewTab();
            this.KeyPreview = true;

            // Initialize Roslyn service (optional; falls back gracefully if it fails)
            try
            {
                roslynService = new RoslynIntellisenseService();
            }
            catch
            {
                roslynService = null;
            }

            _aiAgent = new AiAgent();

            InitializeCollabMenu();
        }

        private void InitializeCollabMenu()
        {
            var collabMenu = new ToolStripMenuItem("Collab");
            var startServerItem = new ToolStripMenuItem("Start Server", null, StartServer_Click);
            var connectClientItem = new ToolStripMenuItem("Connect Client", null, ConnectClient_Click);

            collabMenu.DropDownItems.Add(startServerItem);
            collabMenu.DropDownItems.Add(connectClientItem);

            menuStrip1.Items.Add(collabMenu);
        }

        private void StartServer_Click(object sender, EventArgs e)
        {
            try
            {
                string serverDllPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\collab-dotnet-server\bin\Debug\net6.0\collab-dotnet-server.dll"));
                
                if (!File.Exists(serverDllPath))
                {
                    MessageBox.Show("Server DLL not found at: " + serverDllPath, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{serverDllPath}\" --urls http://localhost:5000",
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetDirectoryName(serverDllPath)
                };

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start server: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ConnectClient_Click(object sender, EventArgs e)
        {
            string targetDir = Environment.CurrentDirectory;
            
            // Try to get directory from current tab
            if (tabControl1.SelectedTab != null)
            {
                var data = tabControl1.SelectedTab.Tag as TabData;
                if (data != null && !string.IsNullOrWhiteSpace(data.FilePath))
                {
                    try
                    {
                        if (File.Exists(data.FilePath))
                            targetDir = Path.GetDirectoryName(data.FilePath);
                    }
                    catch { }
                }
            }

            string sessionId = ShowInputDialog("Enter Session ID:", "Connect to Session", "demo");
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            try
            {
                string clientDllPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\collab-dotnet-client\bin\Debug\net6.0\collab-dotnet-client.dll"));

                if (!File.Exists(clientDllPath))
                {
                    MessageBox.Show("Client DLL not found at: " + clientDllPath, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{clientDllPath}\" http://localhost:5000 \"{sessionId}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = targetDir
                };

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start client: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string ShowInputDialog(string text, string caption, string defaultValue = "")
        {
            Form prompt = new Form()
            {
                Width = 400,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = caption,
                StartPosition = FormStartPosition.CenterScreen
            };
            Label textLabel = new Label() { Left = 20, Top = 20, Text = text, Width = 350 };
            TextBox textBox = new TextBox() { Left = 20, Top = 50, Width = 340, Text = defaultValue };
            Button confirmation = new Button() { Text = "Ok", Left = 250, Width = 100, Top = 80, DialogResult = DialogResult.OK };
            confirmation.Click += (sender, e) => { prompt.Close(); };
            prompt.Controls.Add(textBox);
            prompt.Controls.Add(confirmation);
            prompt.Controls.Add(textLabel);
            prompt.AcceptButton = confirmation;

            return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "";
        }

        private void bottomTabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (bottomTabControl.SelectedTab == null) return;
                // If Terminal tab selected, change working directory
                if (bottomTabControl.SelectedTab == tabPageTerminal)
                {
                    string targetDir = null;

                    // Prefer folder of current editor file
                    if (tabControl1.SelectedTab != null)
                    {
                        var data = tabControl1.SelectedTab.Tag as TabData;
                        if (data != null && !string.IsNullOrWhiteSpace(data.FilePath))
                        {
                            try
                            {
                                if (File.Exists(data.FilePath))
                                    targetDir = Path.GetDirectoryName(data.FilePath);
                                else if (!Path.IsPathRooted(data.FilePath))
                                {
                                    // If only a file name, try project dir (current directory)
                                    targetDir = Environment.CurrentDirectory;
                                }
                            }
                            catch { }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(targetDir))
                    {
                        targetDir = Environment.CurrentDirectory;
                    }

                    // If changed, send cd /d command to cmd
                    if (!string.Equals(lastTerminalDir, targetDir, StringComparison.OrdinalIgnoreCase))
                    {
                        lastTerminalDir = targetDir;
                        try
                        {
                            if (cmdInputWriter != null && !cmdProcess.HasExited)
                            {
                                cmdInputWriter.WriteLine("cd /d " + QuotePath(targetDir));
                                cmdInputWriter.Flush();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static string QuotePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "\"\"";
            return "\"" + p.Replace("\"", "\\\"") + "\"";
        }

        private void JumpToLine(int line, int column)
        {
            var editor = GetCurrentTextBox();
            if (editor == null) return;

            // convert to zero-based
            line = Math.Max(1, line) - 1;
            column = Math.Max(1, column) - 1;

            if (line >= editor.Lines.Count)
                return;

            int pos = editor.Lines[line].Position + column;
            pos = Math.Min(pos, editor.TextLength);

            editor.SetSel(pos, pos);
            editor.ScrollCaret();
            editor.Focus();
        }

        private string GetMembersFromType(Type type)
        {
            return string.Join(" ",
                type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m =>
                        m.MemberType == MemberTypes.Method ||
                        m.MemberType == MemberTypes.Property ||
                        m.MemberType == MemberTypes.Field)
                    .Select(m => m.Name)
                    .Distinct()
                    .OrderBy(n => n)
            );
        }

        private void EnableMemberIntellisense(Scintilla editor)
        {
            editor.CharAdded += async (s, e) =>
            {
                if (e.Char != '.')
                    return;

                int pos = editor.CurrentPosition - 1;
                if (pos <= 0)
                    return;

                int wordStart = editor.WordStartPosition(pos, true);
                string word = editor.GetTextRange(wordStart, pos - wordStart);

                if (string.IsNullOrWhiteSpace(word))
                    return;

                // Try Roslyn to get accurate type/member suggestions
                if (roslynService != null)
                {
                    try
                    {
                        // Ask Roslyn for completions at current position
                        var source = editor.Text;
                        var completions = await roslynService.GetCompletionsAsync(source, editor.CurrentPosition).ConfigureAwait(false);
                        if (completions != null && completions.Count > 0)
                        {
                            // Marshal back to UI thread to show completions
                            this.BeginInvoke(new Action(() =>
                            {
                                editor.AutoCSeparator = ' ';
                                editor.AutoCMaxHeight = 12;
                                editor.AutoCOrder = Order.PerformSort;
                                editor.AutoCShow(0, string.Join(" ", completions));
                            }));
                            return;
                        }
                    }
                    catch
                    {
                        // swallow and fallback
                    }
                }

                // Fallback to simple member list for known aliases
                if (!typeAliasMap.TryGetValue(word, out Type type))
                    return;

                string members = GetMembersFromType(type);
                if (string.IsNullOrWhiteSpace(members))
                    return;

                editor.AutoCSeparator = ' ';
                editor.AutoCMaxHeight = 12;
                editor.AutoCOrder = Order.PerformSort;

                editor.AutoCShow(0, members);
            };
        }



        private void OutputBox_MouseClick(object sender, MouseEventArgs e)
        {
            int charIndex = outputTextBox.GetCharIndexFromPosition(e.Location);
            if (charIndex < 0) return;

            int lineIndex = outputTextBox.GetLineFromCharIndex(charIndex);
            if (lineIndex < 0) return;

            if (lastErrors == null || lastErrors.Count == 0 || lastErrorIdMap == null || lastErrorIdMap.Count == 0)
                return;

            string lineText = outputTextBox.Lines[lineIndex];
            // look for marker [ERR:xxxxxxxx]
            var m = Regex.Match(lineText, "\\[ERR:([0-9a-fA-F]{8})\\]");
            if (!m.Success) return;
            string id = m.Groups[1].Value;
            if (lastErrorIdMap.TryGetValue(id, out int errIdx))
            {
                if (errIdx >= 0 && errIdx < lastErrors.Count)
                {
                    var error = lastErrors[errIdx];
                    JumpToLine(error.Line, error.Column);
                }
            }
        }

        private void EnableIntellisense(Scintilla editor)
        {
            editor.AutoCSeparator = ' ';
            editor.AutoCMaxHeight = 10;
            editor.AutoCOrder = Order.PerformSort;

            editor.CharAdded += async (s, e) =>
            {
                // Only trigger for letters
                if (!char.IsLetter((char)e.Char))
                    return;

                int currentPos = editor.CurrentPosition;
                int wordStart = editor.WordStartPosition(currentPos, true);
                int lenEntered = currentPos - wordStart;

                // Avoid annoying popups for very short prefixes
                if (lenEntered < 2)
                    return;

                // Prefer Roslyn completions if available
                if (roslynService != null)
                {
                    try
                    {
                        var source = editor.Text;
                        var completions = await roslynService.GetCompletionsAsync(source, currentPos).ConfigureAwait(false);
                        if (completions != null && completions.Count > 0)
                        {
                            this.BeginInvoke(new Action(() =>
                            {
                                editor.AutoCShow(lenEntered, string.Join(" ", completions));
                            }));
                            return;
                        }
                    }
                    catch
                    {
                        // swallow and fallback to keyword list
                    }
                }

                // Fallback to static keyword list
                editor.AutoCShow(lenEntered, csharpKeywords);
            };
        }



        private void ConfigureCSharpSyntax(Scintilla textBox)
        {
            textBox.Lexer = Lexer.Cpp;
            textBox.SetKeywords(0, "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while");
            textBox.Styles[Style.Cpp.Default].ForeColor = Color.Silver;
            textBox.Styles[Style.Cpp.Comment].ForeColor = Color.Green;
            textBox.Styles[Style.Cpp.CommentLine].ForeColor = Color.Green;
            textBox.Styles[Style.Cpp.CommentDoc].ForeColor = Color.Gray;
            textBox.Styles[Style.Cpp.Number].ForeColor = Color.Olive;
            textBox.Styles[Style.Cpp.Word].ForeColor = Color.Blue;
            textBox.Styles[Style.Cpp.String].ForeColor = Color.Brown;
            textBox.Styles[Style.Cpp.Character].ForeColor = Color.Brown;
            textBox.Styles[Style.Cpp.Operator].ForeColor = Color.Purple;
            textBox.Styles[Style.Cpp.Preprocessor].ForeColor = Color.Maroon;
            // Do not call StyleClearAll here - it would overwrite per-style settings
        }

        private void ConfigurePythonSyntax(Scintilla editor)
        {
            editor.StyleResetDefault();
            editor.Styles[Style.Default].Font = "Consolas";
            editor.Styles[Style.Default].Size = 11;
            editor.Styles[Style.Default].ForeColor = Color.Black;
            editor.Styles[Style.Default].BackColor = Color.White;
            editor.StyleClearAll();

            editor.Lexer = Lexer.Python;

            // Python keywords
            editor.SetKeywords(0,
                "False None True and as assert break class continue def del elif else except finally for "
              + "from global if import in is lambda nonlocal not or pass raise return try while with yield");

            // Comments
            editor.Styles[Style.Python.CommentLine].ForeColor = Color.Green;

            // Strings
            editor.Styles[Style.Python.String].ForeColor = Color.Brown;
            editor.Styles[Style.Python.Character].ForeColor = Color.Brown;

            // Numbers
            editor.Styles[Style.Python.Number].ForeColor = Color.DarkCyan;

            // Keywords
            editor.Styles[Style.Python.Word].ForeColor = Color.Blue;
            editor.Styles[Style.Python.Word].Bold = true;

            // Class & function names
            editor.Styles[Style.Python.ClassName].ForeColor = Color.Teal;
            editor.Styles[Style.Python.DefName].ForeColor = Color.Teal;

            // Operators
            editor.Styles[Style.Python.Operator].ForeColor = Color.Purple;
        }

        private void ConfigureSyntaxByLanguage(Scintilla editor, string language)
        {
            editor.CharAdded -= PythonCharAdded;
            if (language == "Python")
            {
                ConfigurePythonSyntax(editor);
                ConfigurePythonIndentation(editor);
            }
            else if (language == "CSharp")
            {
                ConfigureCSharpSyntax(editor);
                EnableAutoIndent(editor);
            }
        }


        private Scintilla GetCurrentTextBox()
        {
            if (tabControl1.SelectedTab != null)
            {
                return tabControl1.SelectedTab.Controls[0] as Scintilla;
            }
            return null;
        }

        private ScintillaNET.Scintilla GetCurrentEditor()
        {
            if (tabControl1.SelectedTab == null)
                return null;

            return tabControl1.SelectedTab.Controls[0] as ScintillaNET.Scintilla;
        }

        private void CreateNewTab(string fileName = null)
        {
            TabPage newTab = new TabPage(fileName ?? $"Untitled {tabCounter++}");
            Scintilla textBox = new Scintilla
            {
                Dock = DockStyle.Fill
            };
            // Wire up events similar to previous RichTextBox usage
            textBox.TextChanged += textBoxEditor_TextChanged;
            textBox.MouseUp += textBoxEditor_MouseUp;
            textBox.KeyUp += textBoxEditor_KeyUp;

            newTab.Controls.Add(textBox);
            var data = new TabData
            {
                FilePath = fileName ?? string.Empty,
                IsModified = false,
                Language = DetectLanguage(fileName)
            };
            newTab.Tag = data;

            tabControl1.TabPages.Add(newTab);
            tabControl1.SelectedTab = newTab;

            ApplyCurrentTheme(textBox);
            ConfigureSyntaxByLanguage(textBox, data.Language);
            EnableBraceMatching(textBox);
            EnableAutoIndent(textBox);
            EnableCodeFolding(textBox);
            EnableFoldClick(textBox);
            EnableIntellisense(textBox);
            EnableMemberIntellisense(textBox);


            WriteOutput("Editor initialized successfully.");
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Ctrl+N: New tab
            if (keyData == (Keys.Control | Keys.N))
            {
                newToolStripMenuItem_Click(null, null);
                return true;
            }

            // Ctrl+Space: Help Box
            if (keyData == (Keys.Control | Keys.Space))
            {
                var editor = GetCurrentTextBox();
                if (editor != null)
                {
                    int pos = editor.CurrentPosition;
                    int start = editor.WordStartPosition(pos, true);
                    int len = pos - start;

                    editor.AutoCShow(len, csharpKeywords);
                    return true;
                }
            }

            // Ctrl+E: Toggle Explorer
            if (keyData == (Keys.Control | Keys.E))
            {
                explorerToolStripMenuItem.Checked = !explorerToolStripMenuItem.Checked;
                explorerToolStripMenuItem_Click(null, null);
                return true;
            }

            // Ctrl+D: Toggle Chat Window
            if (keyData == (Keys.Control | Keys.D))
            {
                sidebarToolStripMenuItem.Checked = !sidebarToolStripMenuItem.Checked;
                sidebarToolStripMenuItem_Click(null, null);
                return true;
            }


            // Ctrl+W: Close current tab
            if (keyData == (Keys.Control | Keys.W))
            {
                CloseCurrentTab();
                return true;
            }
            // Ctrl+Tab: Next tab
            if (keyData == (Keys.Control | Keys.Tab))
            {
                if (tabControl1.TabCount > 0)
                {
                    int nextIndex = (tabControl1.SelectedIndex + 1) % tabControl1.TabCount;
                    tabControl1.SelectedIndex = nextIndex;
                }
                return true;
            }
            // Ctrl+Shift+Tab: Previous tab
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                if (tabControl1.TabCount > 0)
                {
                    int prevIndex = tabControl1.SelectedIndex - 1;
                    if (prevIndex < 0) prevIndex = tabControl1.TabCount - 1;
                    tabControl1.SelectedIndex = prevIndex;
                }
                return true;
            }

            // Ctrl+S: Save
            if (keyData == (Keys.Control | Keys.S))
            {
                saveToolStripMenuItem_Click(null, null);
            }

            if (keyData == Keys.F5)
            {
                runToolStripMenuItem_Click(null, null);
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CloseCurrentTab()
        {
            if (tabControl1.TabPages.Count > 0 && tabControl1.SelectedTab != null)
            {
                TabPage currentTab = tabControl1.SelectedTab;
                TabData data = currentTab.Tag as TabData;

                if (data != null && data.IsModified)
                {
                    DialogResult result = MessageBox.Show(
                        $"Do you want to save changes to '{currentTab.Text}'?",
                        "Confirm Close",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning);

                    if (result == DialogResult.Yes)
                    {
                        saveToolStripMenuItem_Click(null, null);
                    }
                    else if (result == DialogResult.Cancel)
                    {
                        return;
                    }
                }

                tabControl1.TabPages.Remove(currentTab);

                if (tabControl1.TabPages.Count == 0)
                {
                    CreateNewTab();
                }
            }
        }

        private void CloseAllTabs()
        {
            while (tabControl1.TabPages.Count > 0)
            {
                tabControl1.SelectedTab = tabControl1.TabPages[0];
                CloseCurrentTab();
            }
        }

        private string DetectLanguage(string filePath)
        {
            // Make null-safe: Path.GetExtension(null) may return null, avoid calling ToLowerInvariant() on null
            string ext = Path.GetExtension(filePath ?? string.Empty)?.ToLowerInvariant();
            switch (ext)
            {
                case ".cs":
                    return "C#";
                case ".py":
                    return "Python";
                case ".txt":
                    return "Text";
                default:
                    return "Python";
            }   
        }

        private void newToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CreateNewTab();
        }

        private void newFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Create a new untitled tab (same as newToolStripMenuItem)
            CreateNewTab();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            // Corrected filter: separate entries properly and include All Files option
            openFileDialog.Filter = "Text Files (*.txt)|*.txt|Python Files (*.py)|*.py|C# Files (*.cs)|*.cs";

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                string filePath = openFileDialog.FileName;
                string fileName = Path.GetFileName(filePath);

                CreateNewTab(fileName);
                Scintilla textBox = GetCurrentTextBox();
                if (textBox != null)
                {
                    textBox.Text = File.ReadAllText(filePath);
                    TabData data = tabControl1.SelectedTab.Tag as TabData;
                    if (data != null)
                    {
                        data.FilePath = filePath;
                        data.IsModified = false;
                    }
                    this.Text = "Simple Text Editor - " + fileName;
                }
            }
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (tabControl1.SelectedTab == null) return;

            TabData data = tabControl1.SelectedTab.Tag as TabData;
            Scintilla textBox = GetCurrentTextBox();

            if (textBox == null) return;

            if (String.IsNullOrEmpty(data.FilePath))
            {
                SaveAs();
            }
            else
            {
                File.WriteAllText(data.FilePath, textBox.Text);
                data.IsModified = false;
                UpdateTabTitle();
            }
        }

        private void SaveAs()
        {
            if (tabControl1.SelectedTab == null) return;

            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*";

            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                Scintilla textBox = GetCurrentTextBox();
                if (textBox != null)
                {
                    string filePath = saveFileDialog.FileName;
                    File.WriteAllText(filePath, textBox.Text);

                    TabData data = tabControl1.SelectedTab.Tag as TabData;
                    if (data != null)
                    {
                        data.FilePath = filePath;
                        data.IsModified = false;
                    }

                    tabControl1.SelectedTab.Text = Path.GetFileName(filePath);
                    this.Text = Path.GetFileName(filePath) + " - SimpleTextEditor";
                }
            }
        }

        private void autoSaveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (autoSaveToolStripMenuItem.Checked)
            {
                autoSaveTimer.Start();
            }
            else
            {
                autoSaveTimer.Stop();
            }
        }

        private void autoSaveTimer_Tick(object sender, EventArgs e)
        {
            foreach (TabPage tab in tabControl1.TabPages)
            {
                TabData data = tab.Tag as TabData;
                if (data != null && data.IsModified && !string.IsNullOrEmpty(data.FilePath))
                {
                    Scintilla textBox = tab.Controls.OfType<Scintilla>().FirstOrDefault();
                    if (textBox != null)
                    {
                        try
                        {
                            File.WriteAllText(data.FilePath, textBox.Text);
                            data.IsModified = false;
                            
                            string fileName = Path.GetFileName(data.FilePath);
                            tab.Text = fileName;
                            
                            if (tabControl1.SelectedTab == tab)
                            {
                                this.Text = fileName + " - SimpleTextEditor";
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private void UpdateTabTitle()
        {
            if (tabControl1.SelectedTab != null)
            {
                TabData data = tabControl1.SelectedTab.Tag as TabData;
                if (data != null)
                {
                    string fileName = String.IsNullOrEmpty(data.FilePath)
                        ? tabControl1.SelectedTab.Text.TrimEnd('*', ' ')
                        : Path.GetFileName(data.FilePath);

                    tabControl1.SelectedTab.Text = data.IsModified ? fileName + " *" : fileName;
                }
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void cutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Scintilla textBox = GetCurrentTextBox();
            if (textBox != null) textBox.Cut();
        }

        private void copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Scintilla textBox = GetCurrentTextBox();
            if (textBox != null) textBox.Copy();
        }

        private void pasteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Scintilla textBox = GetCurrentTextBox();
            if (textBox != null) textBox.Paste();
        }

        private void textBoxEditor_TextChanged(object sender, EventArgs e)
        {
            if (tabControl1.SelectedTab != null)
            {
                TabData data = tabControl1.SelectedTab.Tag as TabData;
                if (data != null)
                {
                    data.IsModified = true;
                    UpdateTabTitle();
                }
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            foreach (TabPage tab in tabControl1.TabPages)
            {
                TabData data = tab.Tag as TabData;
                if (data != null && data.IsModified)
                {
                    DialogResult result = MessageBox.Show(
                        $"Do you want to save changes to '{tab.Text}'?",
                        "Confirm Exit",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning);

                    if (result == DialogResult.Yes)
                    {
                        tabControl1.SelectedTab = tab;
                        saveToolStripMenuItem_Click(sender, e);
                    }
                    else if (result == DialogResult.Cancel)
                    {
                        e.Cancel = true;
                        return;
                    }
                }

                try
                {
                    if (cmdProcess != null && !cmdProcess.HasExited)
                        cmdProcess.Kill();
                }
                catch { }

                try
                {
                    if (collabClientProcess != null && !collabClientProcess.HasExited)
                        collabClientProcess.Kill();
                }
                catch { }

                try
                {
                    if (collabServerProcess != null && !collabServerProcess.HasExited)
                        collabServerProcess.Kill();
                }
                catch { }

                SaveSession();

                base.OnFormClosing(e);
            }

            // Dispose Roslyn service if initialized
            try { roslynService?.Dispose(); } catch { }
        }

        private void textBoxEditor_MouseUp(object sender, MouseEventArgs e)
        {
            UpdateCursorPosition();
        }

        private void textBoxEditor_KeyUp(object sender, KeyEventArgs e)
        {
            UpdateCursorPosition();
        }

        private void UpdateCursorPosition()
        {
            Scintilla textBox = GetCurrentTextBox();
            if (textBox != null)
            {
                int index = textBox.CurrentPosition;
                int line = textBox.LineFromPosition(index);
                int column = index - textBox.Lines[line].Position;
                int total_chars = textBox.Text.Length;

                statusLnBox.Text = (line + 1).ToString();
                statusColBox.Text = (column + 1).ToString();
                statusTotBox.Text = total_chars.ToString();
            }
        }

        private void statusNumberBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;

                Scintilla textBox = GetCurrentTextBox();
                if (textBox == null) return;

                int line = 0;
                int col = 0;

                int.TryParse(statusLnBox.Text, out line);
                int.TryParse(statusColBox.Text, out col);

                // convert to zero-based
                line = Math.Max(1, line) - 1;
                col = Math.Max(1, col) - 1;

                // get char index for line
                if (line >= 0 && line <= textBox.Lines.Count - 1)
                {
                    int charIndex = textBox.Lines[line].Position;
                    if (charIndex >= 0)
                    {
                        int targetIndex = charIndex + col;
                        targetIndex = Math.Min(targetIndex, textBox.Text.Length);
                        targetIndex = Math.Max(0, targetIndex);
                        // set caret position
                        textBox.SetSel(targetIndex, targetIndex);
                        textBox.ScrollCaret();
                        textBox.Focus();
                    }
                }
            }
        }

        private void undoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Scintilla textBox = GetCurrentTextBox();
            if (textBox != null && textBox.CanUndo)
            {
                textBox.Undo();
            }
        }

        private void redoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Scintilla textBox = GetCurrentTextBox();
            if (textBox != null && textBox.CanRedo)
            {
                textBox.Redo();
            }
        }

        private void darkThemeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (darkThemeToolStripMenuItem.Checked)
            {
                EnableDarkMode();
            }
            else
            {
                DisableDarkMode();
            }
        }

        private void ApplyCurrentTheme(Scintilla textBox)
        {
            if (darkThemeToolStripMenuItem.Checked)
            {
                textBox.BackColor = Color.FromArgb(40, 40, 40);
                textBox.ForeColor = Color.Gainsboro;
                textBox.Styles[Style.Default].BackColor = textBox.BackColor;
                textBox.Styles[Style.Default].ForeColor = textBox.ForeColor;
                textBox.StyleClearAll();
            }
            else
            {
                textBox.BackColor = Color.White;
                textBox.ForeColor = Color.Black;
                textBox.Styles[Style.Default].BackColor = textBox.BackColor;
                textBox.Styles[Style.Default].ForeColor = textBox.ForeColor;
                textBox.StyleClearAll();
            }
        }

        private void EnableDarkMode()
        {
            this.BackColor = Color.FromArgb(30, 30, 30);
            menuStrip1.BackColor = Color.FromArgb(45, 45, 45);
            menuStrip1.ForeColor = Color.White;
            statusStrip1.BackColor = Color.FromArgb(45, 45, 45);
            statusLabelLn.ForeColor = Color.White;
            statusLabelCol.ForeColor = Color.White;
            statusLabelTot.ForeColor = Color.White;
            tabControl1.BackColor = Color.FromArgb(45, 45, 45);

            foreach (TabPage tab in tabControl1.TabPages)
            {
                Scintilla textBox = tab.Controls[0] as Scintilla;
                if (textBox != null)
                {
                    textBox.BackColor = Color.FromArgb(40, 40, 40);
                    textBox.ForeColor = Color.Gainsboro;
                    textBox.Styles[Style.Default].BackColor = textBox.BackColor;
                    textBox.Styles[Style.Default].ForeColor = textBox.ForeColor;
                    textBox.StyleClearAll();
                }
            }
        }

        private void DisableDarkMode()
        {
            this.BackColor = SystemColors.Control;
            menuStrip1.BackColor = SystemColors.Control;
            menuStrip1.ForeColor = Color.Black;
            statusStrip1.BackColor = SystemColors.Control;
            statusLabelLn.ForeColor = Color.Black;
            statusLabelCol.ForeColor = Color.Black;
            statusLabelTot.ForeColor = Color.Black;
            tabControl1.BackColor = SystemColors.Control;

            foreach (TabPage tab in tabControl1.TabPages)
            {
                Scintilla textBox = tab.Controls[0] as Scintilla;
                if (textBox != null)
                {
                    textBox.BackColor = Color.White;
                    textBox.ForeColor = Color.Black;
                    textBox.Styles[Style.Default].BackColor = textBox.BackColor;
                    textBox.Styles[Style.Default].ForeColor = textBox.ForeColor;
                    textBox.StyleClearAll();
                }
            }
        }

        private void EnableBraceMatching(Scintilla editor)
        {
            editor.UpdateUI += (s, e) =>
            {
                int pos = editor.CurrentPosition;
                int bracePos1 = -1;
                int bracePos2 = -1;

                // Check character before caret
                if (pos > 0 && IsBrace((char)editor.GetCharAt(pos - 1)))
                    bracePos1 = pos - 1;
                // Check character at caret
                else if (IsBrace((char)editor.GetCharAt(pos)))
                    bracePos1 = pos;

                if (bracePos1 >= 0)
                {
                    bracePos2 = editor.BraceMatch(bracePos1);

                    if (bracePos2 >= 0)
                        editor.BraceHighlight(bracePos1, bracePos2);
                    else
                        editor.BraceBadLight(bracePos1);
                }
                else
                {
                    editor.BraceHighlight(-1, -1);
                }
            };
        }

        private bool IsBrace(char ch)
        {
            switch (ch)
            {
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                    return true;
                default:
                    return false;
            }
        }

        private void EnableAutoIndent(Scintilla editor)
        {
            editor.IndentationGuides = IndentView.LookBoth;
            editor.TabWidth = 4;
            editor.UseTabs = false;
            editor.IndentWidth = 4;

            editor.CharAdded += (s, e) =>
            {
                var scint = (Scintilla)s;
                char addedChar = (char)e.Char;

                // Auto-close pairs for common brackets/quotes
                char? closingChar = null;
                switch (addedChar)
                {
                    case '{': closingChar = '}'; break;
                    case '(': closingChar = ')'; break;
                    case '[': closingChar = ']'; break;
                    case '"': closingChar = '"'; break;
                    case '\'': closingChar = '\''; break;
                }

                if (closingChar.HasValue)
                {
                    int pos = scint.CurrentPosition;
                    // don't insert if next char is already the closing char
                    int next = pos < scint.TextLength ? scint.GetCharAt(pos) : -1;
                    if (next != closingChar.Value)
                    {
                        scint.BeginUndoAction();
                        scint.InsertText(pos, closingChar.Value.ToString());
                        // keep caret between
                        scint.SetSel(pos, pos);
                        scint.EndUndoAction();
                    }
                    return; // done handling char-added for bracket
                }

                // Handle newline (avoid double-handling CRLF)
                bool isNewLine = e.Char == '\n' || (e.Char == '\r' && (scint.CurrentPosition >= scint.TextLength || scint.GetCharAt(scint.CurrentPosition) != '\n'));
                if (isNewLine)
                {
                    int newLine = scint.LineFromPosition(scint.CurrentPosition);
                    if (newLine <= 0) return;

                    int prevLine = newLine - 1;
                    string prevLineText = scint.Lines[prevLine].Text;

                    // base indentation from previous line
                    int indent = scint.Lines[prevLine].Indentation;
                    // If Indentation property isn't set, compute from leading whitespace
                    if (indent == 0)
                    {
                        indent = GetIndentation(prevLineText, scint);
                    }

                    // increase after an opening brace
                    if (prevLineText.TrimEnd().EndsWith("{"))
                        indent += scint.IndentWidth;

                    // if the next non-whitespace character at the new line is a closing brace, implement VS-style block formatting
                    int pos = scint.CurrentPosition;
                    int nextChar = pos < scint.TextLength ? scint.GetCharAt(pos) : -1;
                    if (nextChar == '}')
                    {
                        // insert indentation for the inner line and a new line before existing closing brace
                        string innerIndent = new string(' ', indent);
                        string outerIndent = new string(' ', Math.Max(0, indent - scint.IndentWidth));

                        scint.BeginUndoAction();
                        // insert inner indentation and a newline+outer indentation before the existing '}'
                        // caret is currently at start of inner line; insert innerIndent + \n + outerIndent so that existing '}' moves to next line after outerIndent
                        scint.InsertText(pos, innerIndent + "\n" + outerIndent);

                        // set indentation properties for the lines
                        scint.Lines[newLine].Indentation = indent;
                        scint.Lines[newLine + 1].Indentation = Math.Max(0, indent - scint.IndentWidth);

                        // place caret at end of inner indentation
                        scint.SetSel(pos + innerIndent.Length, pos + innerIndent.Length);
                        scint.EndUndoAction();

                        return;
                    }

                    // otherwise just set line indentation to inherited value
                    scint.Lines[newLine].Indentation = indent;

                    return;
                }

                // Auto outdent when '}' is typed
                if (e.Char == '}')
                {
                    int pos = scint.CurrentPosition;
                    int line = scint.LineFromPosition(pos);

                    // position of the just-typed brace is pos - 1
                    int bracePos = scint.BraceMatch(pos - 1);
                    if (bracePos >= 0)
                    {
                        int matchLine = scint.LineFromPosition(bracePos);
                        int desiredIndent = scint.Lines[matchLine].Indentation;
                        if (desiredIndent == 0)
                            desiredIndent = GetIndentation(scint.Lines[matchLine].Text, scint);
                        scint.Lines[line].Indentation = desiredIndent;
                    }
                }
            };
        }

        private void ConfigurePythonIndentation(Scintilla editor)
        {
            editor.UseTabs = false;
            editor.TabWidth = 4;
            editor.IndentWidth = 4;

            editor.IndentationGuides = IndentView.LookBoth;

            editor.CharAdded -= PythonCharAdded;
            editor.CharAdded += PythonCharAdded;
        }

        private void PythonCharAdded(object sender, CharAddedEventArgs e)
        {
            var editor = (Scintilla)sender;

            // Only care about newline and typing that may trigger dedent
            if (e.Char == '\n')
            {
                int currentLine = editor.LineFromPosition(editor.CurrentPosition);
                if (currentLine <= 0) return;

                // Find previous non-blank line
                int prevLine = currentLine - 1;
                while (prevLine >= 0 && string.IsNullOrWhiteSpace(editor.Lines[prevLine].Text)) prevLine--;
                if (prevLine < 0)
                {
                    editor.Lines[currentLine].Indentation = 0;
                    return;
                }

                string prevText = editor.Lines[prevLine].Text;

                // Compute indentation (count spaces and tabs)
                int prevIndent = 0;
                foreach (char c in prevText)
                {
                    if (c == ' ') prevIndent++;
                    else if (c == '\t') prevIndent += editor.TabWidth;
                    else break;
                }

                int indent = prevIndent;

                // Increase indent after a colon (block opener)
                if (prevText.TrimEnd().EndsWith(":"))
                {
                    indent = prevIndent + editor.IndentWidth;
                }

                editor.Lines[currentLine].Indentation = indent;
                return;
            }

            // When typing letters/spaces at start of a line, detect dedent keywords and adjust indentation
            if (!char.IsLetterOrDigit((char)e.Char) && e.Char != '_')
                return;

            int lineIndex = editor.LineFromPosition(editor.CurrentPosition);
            if (lineIndex < 0) return;

            string lineText = editor.Lines[lineIndex].Text;
            string trimmed = lineText.TrimStart();
            if (string.IsNullOrEmpty(trimmed)) return;

            // Dedent keywords that should align with the parent block
            string[] dedentKeywords = new[] { "elif", "else", "except", "finally" };

            foreach (var kw in dedentKeywords)
            {
                if (trimmed.StartsWith(kw + " ") || trimmed.Equals(kw) || trimmed.StartsWith(kw + ":"))
                {
                    // Find previous non-blank line before this line
                    int prevLine = lineIndex - 1;
                    while (prevLine >= 0 && string.IsNullOrWhiteSpace(editor.Lines[prevLine].Text)) prevLine--;
                    int targetIndent = 0;
                    if (prevLine >= 0)
                    {
                        string prevText = editor.Lines[prevLine].Text;
                        // compute indent of previous non-blank line (as spaces)
                        int prevIndent = 0;
                        foreach (char c in prevText)
                        {
                            if (c == ' ') prevIndent++;
                            else if (c == '\t') prevIndent += editor.TabWidth;
                            else break;
                        }

                        // If previous line is a block opener (ends with ':'), the parent block indent is prevIndent
                        // so dedent keywords should align with prevIndent
                        // Otherwise just use prevIndent
                        targetIndent = prevIndent;
                    }

                    // Apply dedent only if current indentation is greater than target
                    if (editor.Lines[lineIndex].Indentation > targetIndent)
                    {
                        editor.Lines[lineIndex].Indentation = targetIndent;
                    }

                    break;
                }
            }
        }

        private int GetIndentation(string line, Scintilla editor)
        {
            int count = 0;
            foreach (char c in line)
            {
                if (c == ' ')
                    count++;
                else if (c == '\t')
                    count += editor.TabWidth;
                else
                    break;
            }
            return count;
        }

        private void EnableCodeFolding(Scintilla editor)
        {
            editor.SetProperty("fold", "1");
            editor.SetProperty("fold.compact", "1");

            editor.SetProperty("fold.comment", "1");
            editor.SetProperty("fold.preprocessor", "1");

            editor.Margins[2].Type = MarginType.Symbol;
            editor.Margins[2].Mask = Marker.MaskFolders;
            editor.Margins[2].Sensitive = true;
            editor.Margins[2].Width = 20;

            editor.Markers[Marker.Folder].Symbol = MarkerSymbol.BoxPlus;
            editor.Markers[Marker.FolderOpen].Symbol = MarkerSymbol.BoxMinus;
            editor.Markers[Marker.FolderEnd].Symbol = MarkerSymbol.BoxPlusConnected;
            editor.Markers[Marker.FolderMidTail].Symbol = MarkerSymbol.TCorner;
            editor.Markers[Marker.FolderOpenMid]. Symbol = MarkerSymbol.BoxMinusConnected;
            editor.Markers[Marker.FolderSub].Symbol = MarkerSymbol.VLine;
            editor.Markers[Marker.FolderTail].Symbol = MarkerSymbol.LCorner;

            editor.Markers[Marker.Folder].SetBackColor(Color.Gray);
            editor.Markers[Marker.FolderOpen].SetBackColor(Color.Gray);
        }

        private void EnableFoldClick(Scintilla editor)
        {
            editor.MarginClick += (s, e) =>
            {
                if (e.Margin == 2)
                {
                    int line = editor.LineFromPosition(e.Position);
                    editor.Lines[line].ToggleFold();
                }
            };
        }

        private RichTextBox outputTextBox => this.richTextBox1;

        private void ShowOutputPanel()
        {
            mainSplitContainer.Panel2Collapsed = false;
        }

        private void HideOutputPanel()
        {
            mainSplitContainer.Panel2Collapsed = true;
        }

        private void ClearOutput()
        {
            if (outputTextBox == null) return;
            bool wasReadOnly = outputTextBox.ReadOnly;
            outputTextBox.ReadOnly = false;
            outputTextBox.Clear();
            outputTextBox.ReadOnly = wasReadOnly;
        }

        private void WriteOutput(string text)
        {
            if (outputTextBox == null) return;
            ShowOutputPanel();
            bool wasReadOnly = outputTextBox.ReadOnly;
            outputTextBox.ReadOnly = false;
            outputTextBox.SelectionColor = Color.White;
            outputTextBox.AppendText(text + Environment.NewLine);
            outputTextBox.ScrollToCaret();
            outputTextBox.ReadOnly = wasReadOnly;
        }

        private void WriteError(string text)
        {
            if (outputTextBox == null) return;
            ShowOutputPanel();
            bool wasReadOnly = outputTextBox.ReadOnly;
            outputTextBox.ReadOnly = false;
            outputTextBox.SelectionColor = Color.OrangeRed;
            outputTextBox.AppendText(text + Environment.NewLine);
            outputTextBox.ScrollToCaret();
            outputTextBox.ReadOnly = wasReadOnly;
        }

        private void outputToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (outputToolStripMenuItem.Checked)
            {
                ShowOutputPanel();
            }
            else
            {
                HideOutputPanel();
            }
        }

        private async void runToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (isRunning)
            {
                WriteError("A program is already running.");
                return;
            }
            stopToolStripMenuItem.Enabled = true;
            isRunning = true;
            runToolStripMenuItem.Enabled = false;

            ClearOutput();

            var editor = GetCurrentTextBox();
            if (editor == null)
            {
                WriteError("No active editor.");
                ResetRunState();
                return;
            }

            var tabData = tabControl1.SelectedTab.Tag as TabData;
            string filePath = tabData?.FilePath;

            string extension = string.IsNullOrEmpty(filePath)
                ? ".cs"
                : Path.GetExtension(filePath);

            var runner = runners.FirstOrDefault(r => r.CanRun(extension));
            if (runner == null)
            {
                WriteError($"No runner available for {extension}");
                ResetRunState();
                return;
            }

            WriteOutput($"Running {runner.Language}...");
            WriteOutput("================================");

            string tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DevAiRuns"
            );

            try
            {
                // Capture editor text on the UI thread before starting background work
                string code = editor.Text;

                // If the file is a Python file, run via PythonRunner
                if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
                {
                    var pyResult = await Task.Run(() => new PythonRunner().Run(code, tempDir));
                    runningProcess = pyResult.RunningProcess;

                    if (!string.IsNullOrWhiteSpace(pyResult.Output))
                        WriteOutput(pyResult.Output);

                    if (!string.IsNullOrWhiteSpace(pyResult.Errors))
                        WriteError(pyResult.Errors);

                    // Python errors are not parsed with C# parser; clear previous errors
                    lastErrors = new List<CompilerErrorInfo>();
                    lastErrorIdMap.Clear();

                    WriteOutput($"Exit Code: {pyResult.ExitCode}");
                }
                else
                {
                    RunResult result = await Task.Run(() =>
                        runner.Run(code, tempDir)
                    );
                    runningProcess = result.RunningProcess;

                    if (!string.IsNullOrWhiteSpace(result.Output))
                        WriteOutput(result.Output);

                    // Wait for completion if the runner returned a completion task (process still running)
                    if (result.Completion != null)
                    {
                        // Stream incremental output to the UI while waiting
                        WriteOutput("Process started. Waiting for completion...");
                        await result.Completion;

                        // After completion, refresh outputs
                        if (!string.IsNullOrWhiteSpace(result.Output))
                            WriteOutput(result.Output);
                        if (!string.IsNullOrWhiteSpace(result.Errors))
                            WriteError(result.Errors);
                    }

                    // Reset lastErrors and populate only when there are errors
                    lastErrors = new List<CompilerErrorInfo>();
                    lastErrorIdMap.Clear();
                    if (!string.IsNullOrWhiteSpace(result.Errors))
                    {
                        lastErrors = ParseCSharpErrors(result.Errors);

                        // For each parsed error, write it with a stable marker and record the marker->index mapping
                        for (int i = 0; i < lastErrors.Count; i++)
                        {
                            var err = lastErrors[i];
                            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
                            lastErrorIdMap[id] = i;
                            WriteError($"[ERR:{id}] Line {err.Line}, Col {err.Column}: {err.Message}");
                        }
                    }

                    WriteOutput($"Exit Code: {result.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                WriteError(ex.ToString());
            }
            finally
            {
                ResetRunState();
            }
        }

        // Designer references `runToolStripMenuItem1_Click` in Form1.Designer.cs - provide a simple forwarder
        private void runToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            runToolStripMenuItem_Click(sender, e);
        }

        private void openExternalTerminalToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                string path = Environment.CurrentDirectory;
                // If a folder is open, use that
                if (fileTreeView.Nodes.Count > 0 && fileTreeView.Nodes[0].Tag is string rootPath)
                {
                    path = rootPath;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = path
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening terminal: " + ex.Message);
            }
        }

        private Process collabServerProcess;

        private void hostToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (collabServerProcess != null && !collabServerProcess.HasExited)
            {
                MessageBox.Show("Server is already running.");
                return;
            }

            try
            {
                string serverDllPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\collab-dotnet-server\bin\Debug\net6.0\collab-dotnet-server.dll"));
                
                if (!File.Exists(serverDllPath))
                {
                    MessageBox.Show($"Server DLL not found at {serverDllPath}");
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{serverDllPath}\" --urls http://0.0.0.0:5000",
                    UseShellExecute = false,
                    CreateNoWindow = true, // Hide the server window
                    WorkingDirectory = Path.GetDirectoryName(serverDllPath)
                };

                collabServerProcess = Process.Start(psi);
                
                string localIp = GetLocalIPAddress();
                MessageBox.Show($"Session Hosted!\n\nYour Local IP: {localIp}\nPort: 5000\n\nShare this IP with others so they can join.\n\nConnecting you to localhost...", "Host Started");

                // Auto-connect self
                StartClient("localhost");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start host: {ex.Message}");
            }
        }

        private void joinToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Simple input dialog replacement since we don't have VB ref
            string ip = "localhost";
            using (Form inputForm = new Form())
            {
                inputForm.Width = 300;
                inputForm.Height = 150;
                inputForm.Text = "Join Session";
                inputForm.StartPosition = FormStartPosition.CenterParent;
                
                Label lbl = new Label() { Left = 10, Top = 10, Text = "Enter Host IP Address:" };
                TextBox txt = new TextBox() { Left = 10, Top = 35, Width = 260, Text = "localhost" };
                Button btnOk = new Button() { Text = "OK", Left = 190, Width = 80, Top = 70, DialogResult = DialogResult.OK };
                
                inputForm.Controls.Add(lbl);
                inputForm.Controls.Add(txt);
                inputForm.Controls.Add(btnOk);
                inputForm.AcceptButton = btnOk;

                if (inputForm.ShowDialog() == DialogResult.OK)
                {
                    ip = txt.Text;
                }
                else
                {
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(ip)) return;

            StartClient(ip);
        }

        private void StartClient(string ipAddress)
        {
            if (collabClientProcess != null && !collabClientProcess.HasExited)
            {
                MessageBox.Show("Already connected.");
                return;
            }

            string workspace = currentWorkspacePath;
            if (string.IsNullOrEmpty(workspace))
            {
                // Fallback to current directory or ask user
                if (fileTreeView.Nodes.Count > 0 && fileTreeView.Nodes[0].Tag is string rootPath)
                {
                    workspace = rootPath;
                }
                else
                {
                    workspace = Environment.CurrentDirectory;
                }
            }

            // Assume client is in ../collab-dotnet-client/bin/Debug/net6.0/collab-dotnet-client.exe relative to DevAi.exe
            // DevAi.exe is in DevAi/bin/Debug
            string clientPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\collab-dotnet-client\bin\Debug\net6.0\collab-dotnet-client.exe"));
            
            if (!File.Exists(clientPath))
            {
                MessageBox.Show($"Client executable not found at {clientPath}");
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = clientPath,
                    Arguments = $"http://{ipAddress}:5000 demo", 
                    WorkingDirectory = workspace,
                    UseShellExecute = false,
                    CreateNoWindow = true // Hide client window too for cleaner UX
                };

                collabClientProcess = Process.Start(psi);
                MessageBox.Show($"Connected to {ipAddress}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start client: {ex.Message}");
            }
        }

        private string GetLocalIPAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private void disconnectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            bool disconnected = false;
            if (collabClientProcess != null && !collabClientProcess.HasExited)
            {
                try
                {
                    collabClientProcess.Kill();
                    collabClientProcess = null;
                    disconnected = true;
                }
                catch { }
            }

            if (collabServerProcess != null && !collabServerProcess.HasExited)
            {
                try
                {
                    collabServerProcess.Kill();
                    collabServerProcess = null;
                    MessageBox.Show("Host stopped.");
                    disconnected = true;
                }
                catch { }
            }

            if (disconnected)
                MessageBox.Show("Disconnected.");
        }

        private void ResetRunState()
        {
            isRunning = false;
            runToolStripMenuItem.Enabled = true;
            stopToolStripMenuItem.Enabled = false;
            runningProcess = null;
        }

        private void stopToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (runningProcess == null)
            {
                WriteError("No running process to stop.");
                return;
            }

            if (runningProcess.HasExited)
            {
                WriteError("Process has already exited.");
                runningProcess = null;
                ResetRunState();
                return;
            }

            try
            {
                runningProcess.Kill();
                WriteError("Execution stopped by user.");
            }
            catch (Exception ex)
            {
                WriteError("Failed to stop process: " + ex.Message);
            }
            finally
            {
                runningProcess = null;
                ResetRunState();
            }
        }

        private List<CompilerErrorInfo> ParseCSharpErrors(string errorText)
        {
            var errors = new List<CompilerErrorInfo>();

            var regex = new System.Text.RegularExpressions.Regex(
                @"\((\d+),(\d+)\):\s*error\s*[A-Z0-9]+:\s*(.*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (string line in errorText.Split('\n'))
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    errors.Add(new CompilerErrorInfo
                    {
                        Line = int.Parse(match.Groups[1].Value),
                        Column = int.Parse(match.Groups[2].Value),
                        Message = match.Groups[3].Value.Trim()
                    });
                }
            }

            return errors;
        }

        private void StartTerminal()
        {
            cmdProcess = new Process();
            cmdProcess.StartInfo.FileName = "cmd.exe";
            // Use prompt $P$G to show current working directory followed by '>'
            cmdProcess.StartInfo.Arguments = "/K prompt $P$G";
            cmdProcess.StartInfo.UseShellExecute = false;
            cmdProcess.StartInfo.RedirectStandardError = true;
            cmdProcess.StartInfo.RedirectStandardInput = true;
            cmdProcess.StartInfo.RedirectStandardOutput = true;
            cmdProcess.StartInfo.CreateNoWindow = true;

            cmdProcess.OutputDataReceived += Cmd_OutPutReceived;
            cmdProcess.ErrorDataReceived += Cmd_OutPutReceived;

            cmdProcess.Start();

            cmdInputWriter = cmdProcess.StandardInput;

            cmdProcess.BeginOutputReadLine();
            cmdProcess.BeginErrorReadLine();

            // Show a header and the current working directory
            terminalBox.AppendText("DevAi Terminal (cmd)\n");
            try
            {
                terminalBox.AppendText(Environment.CurrentDirectory + "\n\n");
            }
            catch
            {
            }

            // Position caret at end and mark input start index
            terminalBox.SelectionStart = terminalBox.TextLength;
            terminalBox.ScrollToCaret();
            inputStartIndex = terminalBox.TextLength;
            terminalBox.Focus();

            terminalInputBuffer.Clear();
        }

        private void terminalBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (cmdInputWriter == null) return;

            // Ignore control chars except handled ones
            if (e.KeyChar == (char)Keys.Enter || e.KeyChar == (char)Keys.Return)
            {
                // handled in KeyDown
                e.Handled = true;
                return;
            }

            if (e.KeyChar == (char)Keys.Back)
            {
                // KeyDown handles backspace behavior; let KeyDown suppress if needed and maintain buffer
                if (terminalInputBuffer.Length > 0)
                    terminalInputBuffer.Length -= 1;
                e.Handled = false;
                return;
            }

            // Printable characters
            if (!char.IsControl(e.KeyChar))
            {
                // Ensure caret is in input area
                if (terminalBox.SelectionStart < inputStartIndex)
                {
                    terminalBox.SelectionStart = terminalBox.TextLength;
                }

                terminalInputBuffer.Append(e.KeyChar);
                e.Handled = false; // allow character to be inserted into control
            }
        }

        private void terminalBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (cmdInputWriter == null) return;

            // If caret moved before input area, force it to the end (prevent editing history)
            if (terminalBox.SelectionStart < inputStartIndex)
                terminalBox.SelectionStart = terminalBox.TextLength;

            // Handle navigation keys that are allowed even before inputStartIndex
            if (e.KeyCode == Keys.Home)
            {
                // move caret to start of input area
                terminalBox.SelectionStart = inputStartIndex;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                string command = terminalInputBuffer.ToString();

                // Echo a newline in the UI so the command looks submitted
                terminalBox.AppendText(Environment.NewLine);
                terminalBox.SelectionStart = terminalBox.TextLength;
                terminalBox.ScrollToCaret();

                // Send to process
                try
                {
                    cmdInputWriter.WriteLine(command);
                    cmdInputWriter.Flush();
                }
                catch
                {
                    // ignore write failures
                }

                // Update input start position to end of current text and clear buffer
                inputStartIndex = terminalBox.TextLength;
                terminalInputBuffer.Clear();

                e.SuppressKeyPress = true;
                return;
            }

            // Prevent backspace deleting the prompt
            if (e.KeyCode == Keys.Back)
            {
                if (terminalBox.SelectionStart <= inputStartIndex || terminalInputBuffer.Length == 0)
                {
                    e.SuppressKeyPress = true;
                    return;
                }
                // otherwise allow backspace and buffer was adjusted in KeyPress
                return;
            }

            // Prevent selection-based deletes that would modify history before the input area
            if (e.KeyCode == Keys.Delete && terminalBox.SelectionStart < inputStartIndex)
            {
                e.SuppressKeyPress = true;
                return;
            }
        }


        private string GetCurrentCommand()
        {
            if (terminalBox == null) return string.Empty;
            if (terminalBox.TextLength <= inputStartIndex) return string.Empty;
            return terminalBox.Text.Substring(inputStartIndex).Trim();
        }

        private void Cmd_OutPutReceived(object sender, DataReceivedEventArgs e)
        {
            if (e == null || e.Data == null) return;
            try
            {
                this.BeginInvoke(new Action(() =>
                {
                    terminalBox.AppendText(e.Data + Environment.NewLine);
                    terminalBox.SelectionStart = terminalBox.TextLength;
                    terminalBox.ScrollToCaret();

                    // mark the position where user input begins
                    inputStartIndex = terminalBox.TextLength;
                }));
            }
            catch
            {
                // ignore exceptions during shutdown
            }
        }
        private void openFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                if (fbd.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                {
                    LoadFolder(fbd.SelectedPath);
                }
            }
        }

        private FileSystemWatcher workspaceWatcher;

        private void LoadFolder(string path)
        {
            currentWorkspacePath = path;
            fileTreeView.Nodes.Clear();
            var rootNode = new TreeNode(Path.GetFileName(path)) { Tag = path };
            fileTreeView.Nodes.Add(rootNode);
            PopulateTreeView(path, rootNode);
            rootNode.Expand();

            // Setup Workspace Watcher
            if (workspaceWatcher != null)
            {
                workspaceWatcher.Dispose();
                workspaceWatcher = null;
            }

            try
            {
                workspaceWatcher = new FileSystemWatcher(path);
                workspaceWatcher.IncludeSubdirectories = true;
                workspaceWatcher.EnableRaisingEvents = true;
                workspaceWatcher.Created += OnWorkspaceChanged;
                workspaceWatcher.Deleted += OnWorkspaceChanged;
                workspaceWatcher.Renamed += OnWorkspaceChanged;
                workspaceWatcher.Changed += OnFileChanged;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to start workspace watcher: " + ex.Message);
            }

            // Update terminal directory
            try
            {
                if (cmdInputWriter != null && !cmdProcess.HasExited)
                {
                    cmdInputWriter.WriteLine("cd /d " + QuotePath(path));
                    cmdInputWriter.Flush();
                    lastTerminalDir = path;
                }
            }
            catch { }

            // Initialize Chat Watcher
            try
            {
                if (chatWatcher != null)
                {
                    chatWatcher.Dispose();
                    chatWatcher = null;
                }

                chatLogPath = Path.Combine(path, "chat.log");
                chatWatcher = new FileSystemWatcher(path, "chat.log");
                chatWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
                chatWatcher.Changed += ChatWatcher_Changed;
                chatWatcher.Created += ChatWatcher_Changed;
                chatWatcher.EnableRaisingEvents = true;

                ReloadChat();
            }
            catch { }
        }

        private void ChatWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            this.BeginInvoke(new Action(() => ReloadChat()));
        }

        private void ReloadChat()
        {
            try
            {
                if (!File.Exists(chatLogPath)) return;

                using (var fs = new FileStream(chatLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    var content = sr.ReadToEnd();
                    chatHistoryBox.Clear();
                    
                    var lines = content.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("[Image:"))
                        {
                            try
                            {
                                string base64 = line.Substring(7, line.Length - 8); // [Image:...]
                                byte[] bytes = Convert.FromBase64String(base64);
                                using (var ms = new MemoryStream(bytes))
                                {
                                    Image img = Image.FromStream(ms);
                                    Clipboard.SetImage(img);
                                    chatHistoryBox.SelectionStart = chatHistoryBox.TextLength;
                                    chatHistoryBox.Paste();
                                    chatHistoryBox.AppendText("\n");
                                }
                            }
                            catch { }
                        }
                        else
                        {
                            chatHistoryBox.AppendText(line + "\n");
                        }
                    }
                    chatHistoryBox.SelectionStart = chatHistoryBox.TextLength;
                    chatHistoryBox.ScrollToCaret();
                }
            }
            catch { }
        }

        private void PopulateTreeView(string path, TreeNode parentNode)
        {
            try
            {
                var dirs = Directory.GetDirectories(path);
                foreach (var dir in dirs)
                {
                    var node = new TreeNode(Path.GetFileName(dir)) { Tag = dir };
                    parentNode.Nodes.Add(node);
                    PopulateTreeView(dir, node);
                }

                var files = Directory.GetFiles(path);
                foreach (var file in files)
                {
                    var node = new TreeNode(Path.GetFileName(file)) { Tag = file };
                    parentNode.Nodes.Add(node);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { Console.WriteLine(ex.Message); }
        }

        private void fileTreeView_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node.Tag is string path)
            {
                OpenFile(path);
            }
        }

        private void sidebarToolStripMenuItem_Click(object sender, EventArgs e)
        {
            editorChatSplitContainer.Panel2Collapsed = !sidebarToolStripMenuItem.Checked;
        }

        private void explorerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            explorerSplitContainer.Panel1Collapsed = !explorerToolStripMenuItem.Checked;
        }

        private void AppendToChatLog(string text)
        {
            if (string.IsNullOrEmpty(chatLogPath)) return;
            try
            {
                using (var fs = new FileStream(chatLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs))
                {
                    sw.WriteLine(text);
                }
            }
            catch { }
        }

        private void AppendToChat(string text)
        {
            if (chatHistoryBox.InvokeRequired)
            {
                chatHistoryBox.Invoke(new Action<string>(AppendToChat), text);
                return;
            }
            
            chatHistoryBox.AppendText(text + "\n\n");
            chatHistoryBox.ScrollToCaret();
            
            // Also log to file to keep history consistent
            AppendToChatLog(text);
        }

        private void AppendToAgentChat(string text)
        {
            if (agentHistoryBox.InvokeRequired)
            {
                agentHistoryBox.Invoke(new Action<string>(AppendToAgentChat), text);
                return;
            }
            
            agentHistoryBox.AppendText(text + "\n\n");
            agentHistoryBox.ScrollToCaret();
        }

        private async void agentSendButton_Click(object sender, EventArgs e)
        {
            string question = agentInputBox.Text;
            if (string.IsNullOrWhiteSpace(question)) return;

            // Display user message
            AppendToAgentChat($"You: {question}");
            agentInputBox.Clear();
            agentSendButton.Enabled = false;

            // Get Code Context
            string currentCode = "";
            string currentFilePath = "CurrentFile.cs";
            var editor = GetCurrentTextBox();
            if (editor != null)
            {
                currentCode = editor.Text;
                if (tabControl1.SelectedTab?.Tag is TabData data)
                {
                    currentFilePath = data.FilePath;
                }
            }

            AppendToAgentChat("Agent: Thinking...");

            // Call AI
            string answer = await _aiAgent.GetResponseAsync(question, currentCode, currentFilePath);

            // Check for auto-apply command (any intent to change code)
            bool isChangeRequest = question.Trim().StartsWith("/replace", StringComparison.OrdinalIgnoreCase) ||
                                   question.ToLower().Contains("change") ||
                                   question.ToLower().Contains("fix") ||
                                   question.ToLower().Contains("refactor") ||
                                   question.ToLower().Contains("update") ||
                                   question.ToLower().Contains("rewrite") ||
                                   question.ToLower().Contains("implement");

            if (isChangeRequest)
            {
                 string code = ExtractCode(answer);
                 // Only apply if code block was actually found and it looks different
                 if (editor != null && !string.IsNullOrWhiteSpace(code) && code.Trim() != answer.Trim())
                 {
                     editor.Text = code;
                     AppendToAgentChat($"Agent: {answer}");
                     AppendToAgentChat("Agent: Code updated in editor.");
                 }
                 else
                 {
                     AppendToAgentChat($"Agent: {answer}");
                 }
            }
            else
            {
                AppendToAgentChat($"Agent: {answer}");
            }
            
            agentSendButton.Enabled = true;
        }

        private void agentInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                agentSendButton.PerformClick();
                e.SuppressKeyPress = true;
            }
        }

        private void chatSendButton_Click(object sender, EventArgs e)
        {
            string message = chatInputBox.Text;
            if (!string.IsNullOrWhiteSpace(message))
            {
                AppendToChatLog($"User: {message}");
                chatInputBox.Clear();
            }
        }

        private string ExtractCode(string response)
        {
            var match = Regex.Match(response, @"```\w*\s*([\s\S]*?)\s*```");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            return response;
        }

        private void chatEmojiButton_Click(object sender, EventArgs e)
        {
            // Simple emoji picker context menu
            ContextMenuStrip emojiMenu = new ContextMenuStrip();
            string[] emojis = { "😊", "😂", "👍", "❤️", "🎉", "🔥", "🐛", "💻", "🚀", "🤔" };
            
            foreach (var emoji in emojis)
            {
                emojiMenu.Items.Add(emoji, null, (s, args) => 
                {
                    chatInputBox.AppendText(emoji);
                });
            }
            
            emojiMenu.Show(chatEmojiButton, new Point(0, -emojiMenu.Height));
        }

        private void chatAttachButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.gif;*.bmp";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(ofd.FileName);
                        string base64 = Convert.ToBase64String(bytes);
                        AppendToChatLog($"[Image:{base64}]");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error attaching image: " + ex.Message);
                    }
                }
            }
        }

        private void OnWorkspaceChanged(object sender, FileSystemEventArgs e)
        {
            this.Invoke((MethodInvoker)delegate
            {
                RefreshExplorer();
            });
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            this.Invoke((MethodInvoker)delegate
            {
                foreach (TabPage tab in tabControl1.TabPages)
                {
                    if (tab.Tag is TabData data && data.FilePath == e.FullPath)
                    {
                        // If not modified by user, reload automatically
                        if (!data.IsModified)
                        {
                            ReloadTab(tab);
                        }
                    }
                }
            });
        }

        private void RefreshExplorer()
        {
            if (string.IsNullOrEmpty(currentWorkspacePath)) return;
            
            // Simple refresh: reload tree
            // Ideally we should preserve expansion state
            fileTreeView.Nodes.Clear();
            var rootNode = new TreeNode(Path.GetFileName(currentWorkspacePath)) { Tag = currentWorkspacePath };
            fileTreeView.Nodes.Add(rootNode);
            PopulateTreeView(currentWorkspacePath, rootNode);
            rootNode.Expand();
        }

        private void ReloadTab(TabPage tab)
        {
            if (tab.Tag is TabData data && File.Exists(data.FilePath))
            {
                try
                {
                    // Use retry logic for reading file as it might be locked by collab client
                    string text = ReadFileWithRetry(data.FilePath);
                    
                    if (tab.Controls.Count > 0 && tab.Controls[0] is Scintilla scintilla)
                    {
                        var currentPos = scintilla.CurrentPosition;
                        scintilla.Text = text;
                        scintilla.SetSavePoint();
                        scintilla.GotoPosition(currentPos);
                    }
                }
                catch { }
            }
        }

        private string ReadFileWithRetry(string path)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            return File.ReadAllText(path); // Fallback
        }

        private void reloadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RefreshExplorer();
            if (tabControl1.SelectedTab != null)
            {
                ReloadTab(tabControl1.SelectedTab);
            }
        }

        private void chatInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                chatSendButton.PerformClick();
                e.SuppressKeyPress = true;
            }
        }

        private void chatClearButton_Click(object sender, EventArgs e)
        {
            chatHistoryBox.Clear();
        }

        private void OpenFile(string path)
        {
            if (!File.Exists(path)) return;

            // Check if already open
            foreach (TabPage tab in tabControl1.TabPages)
            {
                if (tab.Tag is TabData data && data.FilePath == path)
                {
                    tabControl1.SelectedTab = tab;
                    return;
                }
            }

            // Open file
            CreateNewTab(Path.GetFileName(path));
            Scintilla textBox = GetCurrentTextBox();
            if (textBox != null)
            {
                textBox.Text = ReadFileWithRetry(path);
                TabData data = tabControl1.SelectedTab.Tag as TabData;
                if (data != null)
                {
                    data.FilePath = path;
                    data.IsModified = false;
                }
                this.Text = "DevAi - " + Path.GetFileName(path);
            }
        }

        private void SaveSession()
        {
            try
            {
                var state = new SessionState
                {
                    LastWorkspace = currentWorkspacePath,
                    ChatHistory = chatHistoryBox.Rtf,
                    LastFile = null,
                    OpenFiles = new List<string>()
                };

                foreach (TabPage tab in tabControl1.TabPages)
                {
                    if (tab.Tag is TabData data && !string.IsNullOrEmpty(data.FilePath))
                    {
                        state.OpenFiles.Add(data.FilePath);
                    }
                }

                if (tabControl1.SelectedTab != null && tabControl1.SelectedTab.Tag is TabData selectedData)
                {
                    state.LastFile = selectedData.FilePath;
                }

                string json = JsonConvert.SerializeObject(state);
                File.WriteAllText("session.json", json);
            }
            catch { }
        }

        private void RestoreSession()
        {
            try
            {
                if (File.Exists("session.json"))
                {
                    string json = File.ReadAllText("session.json");
                    var state = JsonConvert.DeserializeObject<SessionState>(json);

                    if (!string.IsNullOrEmpty(state.LastWorkspace) && Directory.Exists(state.LastWorkspace))
                    {
                        LoadFolder(state.LastWorkspace);
                    }
                    else
                    {
                        LoadFolder(Environment.CurrentDirectory);
                    }

                    if (!string.IsNullOrEmpty(state.ChatHistory))
                    {
                        try { chatHistoryBox.Rtf = state.ChatHistory; } catch { chatHistoryBox.Text = ""; }
                    }

                    if (state.OpenFiles != null)
                    {
                        foreach (var file in state.OpenFiles)
                        {
                            if (File.Exists(file))
                            {
                                OpenFile(file);
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(state.LastFile) && File.Exists(state.LastFile))
                    {
                        OpenFile(state.LastFile);
                    }
                }
                else
                {
                    LoadFolder(Environment.CurrentDirectory);
                }
            }
            catch 
            {
                LoadFolder(Environment.CurrentDirectory);
            }
        }

    }

    public class TabData
    {
        public string FilePath { get; set; }
        public bool IsModified { get; set; }
        public string Language { get; set; }
    }
}
