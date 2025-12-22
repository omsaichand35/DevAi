using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.IO;
using System.Threading.Tasks;

namespace CollabClient.Services
{
    public class FileWatcher
    {
        private readonly HubConnection _conn;
        private readonly string _sessionId;
        private readonly string _root;
        private FileSystemWatcher _watcher;

        public FileWatcher(HubConnection conn, string sessionId, string root)
        {
            _conn = conn; _sessionId = sessionId; _root = root;
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = false
            };

            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
        }

        public void Start() => _watcher.EnableRaisingEvents = true;
        public void Stop() => _watcher.EnableRaisingEvents = false;

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            var rel = Path.GetRelativePath(_root, e.FullPath);
            var evt = new { Path = rel, IsDirectory = Directory.Exists(e.FullPath) };
            _ = _conn.InvokeAsync("FileCreate", _sessionId, evt);
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            var rel = Path.GetRelativePath(_root, e.FullPath);
            byte[] fakeUpdate = System.Text.Encoding.UTF8.GetBytes(File.Exists(e.FullPath) ? File.ReadAllText(e.FullPath) : string.Empty);
            _ = _conn.InvokeAsync("FileUpdateBinary", _sessionId, rel, fakeUpdate);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            var rel = Path.GetRelativePath(_root, e.FullPath);
            var evt = new { Path = rel, IsDirectory = false };
            _ = _conn.InvokeAsync("FileDelete", _sessionId, evt);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            var oldRel = Path.GetRelativePath(_root, e.OldFullPath);
            var newRel = Path.GetRelativePath(_root, e.FullPath);
            var evt = new { OldPath = oldRel, NewPath = newRel };
            _ = _conn.InvokeAsync("FileRename", _sessionId, evt);
        }
    }
}
