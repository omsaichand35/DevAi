using CollabServer.Models;
using System.Collections.Concurrent;

namespace CollabServer.Services;

public class SessionManager
{
    private ConcurrentDictionary<string, Session> _sessions = new ConcurrentDictionary<string, Session>();

    public Session GetSession(string id)
    {
        _sessions.TryGetValue(id, out var s);
        return s;
    }

    public Session GetOrCreateSession(string id)
    {
        return _sessions.GetOrAdd(id, (k) => new Session(k, "owner"));
    }

    public bool RemoveSession(string id)
    {
        return _sessions.TryRemove(id, out _);
    }
}
