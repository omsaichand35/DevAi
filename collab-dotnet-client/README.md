# Collab .NET Client

MVP client that connects to the Collab .NET server via SignalR and watches the current folder.

Usage:

1. Build the server and run it.
2. dotnet run --project collab-dotnet-client <server-base-url> <sessionId>

Example:

    dotnet run --project collab-dotnet-client "http://localhost:5000" "my-session"

This client is an MVP: it sends file events and placeholder binary updates. Integrate a proper CRDT (Yjs via y-dotnet or custom CRDT) for real content sync.
