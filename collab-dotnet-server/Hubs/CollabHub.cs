using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using CollabServer.Services;
using CollabServer.Models;

namespace CollabServer.Hubs;

// [Authorize]
public class CollabHub : Hub
{
    private readonly SessionManager _sessions;

    public CollabHub(SessionManager sessions)
    {
        _sessions = sessions;
    }

    private string GetUserId()
    {
        return Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Context.ConnectionId;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        await base.OnConnectedAsync();
    }

    public async Task JoinSession(string sessionId, string role)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
            throw new HubException("Unauthorized");

        var session = _sessions.GetOrCreateSession(sessionId);
        session.AddParticipant(userId, Context.ConnectionId, role);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);

        // Notify others
        await Clients.Group(sessionId).SendAsync("ParticipantJoined", new { UserId = userId, Role = role });

        // Send initial project snapshot (CRDT state) - simplified: sending manifest
        var manifest = session.GetManifest();
        await Clients.Caller.SendAsync("InitialManifest", manifest);
    }

    public async Task LeaveSession(string sessionId)
    {
        var userId = GetUserId();
        var session = _sessions.GetSession(sessionId);
        if (session != null)
        {
            session.RemoveParticipant(userId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
            await Clients.Group(sessionId).SendAsync("ParticipantLeft", userId);
        }
    }

    // File system events
    public async Task FileCreate(string sessionId, FileEvent evt)
    {
        ValidatePermission(sessionId, GetUserId(), Permission.Editor);
        var session = _sessions.GetSession(sessionId);
        session.ApplyFileCreate(evt);
        await Clients.GroupExcept(sessionId, Context.ConnectionId).SendAsync("FileCreated", evt);
    }

    public async Task FileDelete(string sessionId, FileEvent evt)
    {
        ValidatePermission(sessionId, GetUserId(), Permission.Editor);
        var session = _sessions.GetSession(sessionId);
        session.ApplyFileDelete(evt);
        await Clients.GroupExcept(sessionId, Context.ConnectionId).SendAsync("FileDeleted", evt);
    }

    public async Task FileRename(string sessionId, FileRenameEvent evt)
    {
        ValidatePermission(sessionId, GetUserId(), Permission.Editor);
        var session = _sessions.GetSession(sessionId);
        session.ApplyFileRename(evt);
        await Clients.GroupExcept(sessionId, Context.ConnectionId).SendAsync("FileRenamed", evt);
    }

    // CRDT updates for file content (Yjs binary updates forwarded)
    public async Task FileUpdateBinary(string sessionId, string filePath, byte[] update)
    {
        ValidatePermission(sessionId, GetUserId(), Permission.Editor);
        var session = _sessions.GetSession(sessionId);
        session.ApplyBinaryUpdate(filePath, update);
        await Clients.GroupExcept(sessionId, Context.ConnectionId).SendAsync("FileUpdateBinary", filePath, update);
    }

    // Permission / admin actions
    public async Task UpdatePermission(string sessionId, string userId, string role)
    {
        ValidatePermission(sessionId, GetUserId(), Permission.Admin);
        var session = _sessions.GetSession(sessionId);
        session.SetRole(userId, role);
        await Clients.Group(sessionId).SendAsync("PermissionUpdated", new { UserId = userId, Role = role });
    }

    private void ValidatePermission(string sessionId, string userId, Permission needed)
    {
        var session = _sessions.GetSession(sessionId);
        if (session == null)
            throw new HubException("Session not found");

        var role = session.GetRole(userId);
        if (!SessionRoleHelper.HasPermission(role, needed))
            throw new HubException("Permission denied");
    }
}
