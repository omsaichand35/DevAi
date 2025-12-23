using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.IO;
using System.Threading.Tasks;

namespace CollabClientApp
{
    public class CollabClient
    {
        private readonly string _server;
        private readonly string _session;
        private HubConnection? _conn;
        private FileWatcher? _watcher;

        public CollabClient(string server, string session)
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
                
                // Write to disk
                try
                {
                    var fullPath = Path.Combine(Directory.GetCurrentDirectory(), path);
                    
                    // Stop watcher to prevent loop
                    if (_watcher != null) _watcher.Stop();
                    
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                        
                    if (update == null || update.Length == 0)
                    {
                        // Handle empty file or delete? For now just write empty
                        File.WriteAllBytes(fullPath, Array.Empty<byte>());
                    }
                    else
                    {
                        File.WriteAllBytes(fullPath, update);
                    }
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

            // Retry loop for initial connection
            int retries = 0;
            while (true)
            {
                try
                {
                    await _conn.StartAsync();
                    Console.WriteLine("Connected to server");
                    break;
                }
                catch (Exception ex)
                {
                    retries++;
                    if (retries > 5) throw;
                    Console.WriteLine($"Connection failed: {ex.Message}. Retrying in 2s...");
                    await Task.Delay(2000);
                }
            }

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
