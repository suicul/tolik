using System.Net.WebSockets;
using System.Text;

namespace TOLIK.Desktop.Bridge;

/// <summary>
/// Minimal receive-side WebSocket seam. The client loop owns connect/reconnect policy;
/// this interface exists so the loop is deterministically testable headless (no real
/// endpoint, no timing flakiness). The production adapter is a thin ClientWebSocket
/// wrapper — no behavior of its own.
/// </summary>
public interface IExpressionSocket : IDisposable
{
    /// <summary>Performs the WS handshake. Throws on any failure (classified by
    /// <see cref="WebSocketFailure.Classify"/> by the caller).</summary>
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Reads ONE complete text message. Null → the connection closed cleanly.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
}

/// <summary>Production socket: ClientWebSocket, no options, no extensions, no cookies
/// — the loopback handshake needs nothing but the token in the query string.</summary>
public sealed class ClientWebSocketSocket : IExpressionSocket
{
    private const int InitialBufferSize = 16 * 1024;

    private readonly ClientWebSocket _socket = new();
    private byte[] _buffer = new byte[InitialBufferSize];

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return null;
        }

        var builder = new StringBuilder();
        while (true)
        {
            if (_buffer.Length == 0)
            {
                _buffer = new byte[InitialBufferSize];
            }
            WebSocketReceiveResult result;
            try
            {
                result = await _socket
                    .ReceiveAsync(new ArraySegment<byte>(_buffer), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (WebSocketException)
            {
                // Faulted mid-message behaves like a broken connection for the loop.
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            builder.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return builder.ToString();
            }

            if (_buffer.Length < 1024 * 1024)
            {
                // Expression frames are a few hundred bytes; 1 MiB is a paranoia ceiling.
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }
        }
    }

    public void Dispose() => _socket.Dispose();
}
