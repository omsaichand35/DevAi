using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.IO;
using System.Threading.Tasks;

namespace CollabClient.Services
{
    public class CollabService
    {
        private readonly string _server;
        private readonly string _session;
        private HubConnection? _conn;
        private FileWatcher? _watcher;

        public CollabService(string server, string session)
        {
            _server = server; _session = session;
        }

        public async Task StartAsync()
        {
            _conn = new HubConnectionBuilder()
                .WithUrl(new Uri(new Uri(_server), "/hub/collab"), options =>
                {
                    // in MVP allow token-less, but ideally set access token
                })
                .WithAutomaticReconnect()
                .Build();

            _conn.On<string>("InitialManifest", (manifestJson) =>
            {
                Console.WriteLine("Received manifest: " + manifestJson);
            });

            _conn.On<string, byte[]>("FileUpdateBinary", (path, update) =>
            {
                Console.WriteLine($"Received binary update for {path}: {update?.Length ?? 0} bytes");
                // Apply CRDT update locally (not implemented in MVP)
                try
                {
                    var fullPath = Path.Combine(Directory.GetCurrentDirectory(), path);
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    
                    // Temporarily disable watcher to avoid infinite loop
                    if (_watcher != null) _watcher.Stop();
                    
                    File.WriteAllBytes(fullPath, update);
                    Console.WriteLine($"Wrote {update.Length} bytes to {fullPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing file {path}: {ex.Message}");
                }
                finally
                {
                    if (_watcher != null) _watcher.Start();
                }
            });

            _conn.On<object>("FileCreated", (evt) =>
            {
                // Handle file creation if needed, though FileUpdateBinary usually covers content
                Console.WriteLine($"File created event received: {evt}");
            });

            await _conn.StartAsync();
            Console.WriteLine("Connected to server");

            // Join session as editor (for MVP)
            await _conn.InvokeAsync("JoinSession", _session, "Editor");

            // Start file watcher for current directory
            _watcher = new FileWatcher(_conn, _session, Directory.GetCurrentDirectory());
            _watcher.Start();
        }

        public async Task StopAsync()
        {
            if (_watcher != null) _watcher.Stop();
            if (_conn != null)
            {
                await _conn.InvokeAsync("LeaveSession", _session);
                await _conn.StopAsync();
            }
        }
    }
}
