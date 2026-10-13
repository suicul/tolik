using System.IO.Pipes;
using System.Text.Json;
using TOLIK.Contracts;

namespace TOLIK.Agent;

/// <summary>
/// The Privilege Broker client (the C0 spike): the typed round-trip to the broker's
/// named pipe — the SAME user session only (the pipe ACL enforces it server-side).
/// The client never sends anything but the typed BrokerRequest.
/// </summary>
public static class BrokerClient
{
    public static BrokerResponse Call(BrokerRequest request, string? pipeName = null, int timeoutMs = 5000)
    {
        if (!request.IsValid) throw new ArgumentException("The broker request is invalid.", nameof(request));
        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            string.IsNullOrWhiteSpace(pipeName) ? BrokerServer.PipeName : pipeName,
            PipeDirection.InOut);
        pipe.Connect(timeoutMs);
        using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(request));
        writer.Flush();
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var line = reader.ReadLine() ?? throw new InvalidOperationException("The broker closed without answering.");
        return JsonSerializer.Deserialize<BrokerResponse>(line)
            ?? throw new InvalidOperationException("The broker response is malformed.");
    }
}
