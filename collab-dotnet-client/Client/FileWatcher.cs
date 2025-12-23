using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.IO;
using System.Threading.Tasks;
using CollabServer.Models;

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
        try
        {
            var rel = Path.GetRelativePath(_root, e.FullPath);
            bool isDir = Directory.Exists(e.FullPath);
            var evt = new FileEvent { Path = rel, IsDirectory = isDir };
            _ = _conn.InvokeAsync("FileCreate", _sessionId, evt);

            if (!isDir)
            {
                // Send content immediately for new files
                OnChanged(sender, e);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in OnCreated: {ex.Message}");
        }
    }

    private async void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var rel = Path.GetRelativePath(_root, e.FullPath);
            if (Directory.Exists(e.FullPath)) return;

            byte[] content = await ReadFileWithRetryAsync(e.FullPath);
            await _conn.InvokeAsync("FileUpdateBinary", _sessionId, rel, content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing change for {e.FullPath}: {ex.Message}");
        }
    }

    private async Task<byte[]> ReadFileWithRetryAsync(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream())
                {
                    await fs.CopyToAsync(ms);
                    return ms.ToArray();
                }
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
        return Array.Empty<byte>();
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            var rel = Path.GetRelativePath(_root, e.FullPath);
            var evt = new FileEvent { Path = rel, IsDirectory = false };
            _ = _conn.InvokeAsync("FileDelete", _sessionId, evt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in OnDeleted: {ex.Message}");
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            var oldRel = Path.GetRelativePath(_root, e.OldFullPath);
            var newRel = Path.GetRelativePath(_root, e.FullPath);
            var evt = new FileRenameEvent { OldPath = oldRel, NewPath = newRel };
            _ = _conn.InvokeAsync("FileRename", _sessionId, evt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in OnRenamed: {ex.Message}");
        }
    }
}
