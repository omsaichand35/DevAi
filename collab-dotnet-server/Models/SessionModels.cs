using System.Collections.Concurrent;

namespace CollabServer.Models;

public enum Permission { Viewer, Editor, Admin }

public class Participant
{
    public string UserId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public Permission Role { get; set; }
}

public class ProjectManifest
{
    public List<string> Files { get; } = new List<string>();
}

public class Session
{
    public string Id { get; }
    public string Owner { get; }

    private ConcurrentDictionary<string, Participant> _participants = new ConcurrentDictionary<string, Participant>();

    // Simplified: per-file binary CRDT (Yjs) document states stored as byte[] updates
    private ConcurrentDictionary<string, byte[]> _fileState = new ConcurrentDictionary<string, byte[]>();

    public Session(string id, string owner)
    {
        Id = id; Owner = owner;
    }

    public void AddParticipant(string userId, string connId, string role)
    {
        Enum.TryParse<Permission>(role, true, out var p);
        _participants[userId] = new Participant { UserId = userId, ConnectionId = connId, Role = p };
    }

    public void RemoveParticipant(string userId)
    {
        _participants.TryRemove(userId, out _);
    }

    public void SetRole(string userId, string role)
    {
        Enum.TryParse<Permission>(role, true, out var p);
        if (_participants.TryGetValue(userId, out var part))
            part.Role = p;
    }

    public Permission GetRole(string userId)
    {
        if (_participants.TryGetValue(userId, out var part))
            return part.Role;
        return Permission.Viewer;
    }

    public ProjectManifest GetManifest()
    {
        var m = new ProjectManifest();
        foreach (var f in _fileState.Keys)
            m.Files.Add(f);
        return m;
    }

    public void ApplyFileCreate(Models.FileEvent evt)
    {
        _fileState.TryAdd(evt.Path, Array.Empty<byte>());
    }

    public void ApplyFileDelete(Models.FileEvent evt)
    {
        _fileState.TryRemove(evt.Path, out _);
    }

    public void ApplyFileRename(Models.FileRenameEvent evt)
    {
        if (_fileState.TryRemove(evt.OldPath, out var v))
            _fileState[evt.NewPath] = v;
    }

    public void ApplyBinaryUpdate(string filePath, byte[] update)
    {
        // In real implementation we'd apply Yjs update to stored document
        _fileState[filePath] = update; // simplified snapshot
    }
}

public static class SessionRoleHelper
{
    public static bool HasPermission(Permission role, Permission needed)
    {
        if (role == Permission.Admin) return true;
        if (needed == Permission.Viewer) return true;
        if (needed == Permission.Editor && role == Permission.Editor) return true;
        return false;
    }
}
