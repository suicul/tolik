using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace TOLIK.Core.Host;

/// <summary>
/// Realtime event hub (AGENTS.md §29): fans out TaskStarted/TaskProgress/TaskCompleted/
/// TaskFailed/TaskCancelled to all connected authenticated WebSocket clients.
/// Per-user channels: clients receive only their own task events.
/// </summary>
public sealed class RealtimeEventHub : IDisposable
{
    private readonly ConcurrentDictionary<Guid, (Guid UserId, WebSocket Socket, Channel<string> Buffer)> _clients = new();
    /// <summary>taskId -> ownerUserId, stamped when a task is created (privacy: §19, §50).</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _taskOwners = new();

    public void StampTaskOwner(Guid taskId, Guid userId) => _taskOwners[taskId] = userId;

    /// <summary>Broadcasts a serialized event to the task owner's connections only.</summary>
    public void PublishToTaskOwner(Guid taskId, string json)
    {
        if (!_taskOwners.TryGetValue(taskId, out var owner) || owner == Guid.Empty) return;
        PublishToUser(owner, json);
    }

    public bool Register(Guid connectionId, Guid userId, WebSocket socket)
    {
        if (!_clients.TryAdd(connectionId, (userId, socket, Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest }))))
            return false;
        _ = PumpAsync(connectionId);
        return true;
    }

    public void Unregister(Guid connectionId) => _clients.TryRemove(connectionId, out _);

    /// <summary>Broadcasts a serialized event to the owner's connections only (privacy: §19, §50).</summary>
    public void PublishToUser(Guid userId, string json)
    {
        foreach (var entry in _clients)
            if (entry.Value.UserId == userId)
                entry.Value.Buffer.Writer.TryWrite(json);
    }

    private async Task PumpAsync(Guid connectionId)
    {
        if (!_clients.TryGetValue(connectionId, out var client)) return;
        try
        {
            await foreach (var message in client.Buffer.Reader.ReadAllAsync())
            {
                if (client.Socket.State != WebSocketState.Open) break;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "event", payload = JsonSerializer.Deserialize<JsonElement>(message) });
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { _clients.TryRemove(connectionId, out _); }
    }

    public int ConnectedClients => _clients.Count;

    public void Dispose()
    {
        foreach (var entry in _clients.Values)
        {
            entry.Buffer.Writer.TryComplete();
            try
            {
                if (entry.Socket.State == WebSocketState.Open)
                    entry.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "hub_shutdown", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch { }
        }
        _clients.Clear();
    }
}
