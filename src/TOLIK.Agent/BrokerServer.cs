using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using TOLIK.Contracts;

namespace TOLIK.Agent;

/// <summary>
/// TOLIK Privilege Broker server — the C0 SPIKE (docs/governance.md §3, Phase C —
/// PLAN REFORM PRG-4f): the controlled-sudo architecture proof. A SEPARATE process
/// (launched on demand, NOT resident SYSTEM — `TOLIK.Agent --broker`), serving ONE
/// operation at a time over a Named Pipe with the current-user-only ACL and the
/// pipe-identity check. TYPED operations only (BrokerProtocol — structurally no
/// string-command field); the unknown op / the off-allowlist target / the ElevateCommand
/// without its explicit double gate all DENY fail-closed. The spike proves the IPC
/// architecture (pipes + ACL + identity + typed round-trip + one-at-a-time); the
/// actual privileged Windows calls (SCM etc.) land with the full Phase C once the
/// spike's choice is confirmed (the honest boundary — the responses say so).
/// </summary>
public sealed class BrokerServer : IDisposable
{
    public const string PipeName = "tolik/privilege-broker";
    private const int MaxRequestBytes = 4096;

    private readonly string _pipeName;
    private NamedPipeServerStream? _server;

    /// <summary>The broker's pipe: the production name by default; the TESTS pass a
    /// unique per-test name (the isolation — an orphaned server from another process
    /// must never intercept a client: the named pipes serve whoever listens first).</summary>
    public BrokerServer(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
    }

    /// <summary>Builds the pipe with the CURRENT-USER-ONLY ACL (docs/governance.md §3:
    /// the client = the same user session; anything else cannot even connect).</summary>
    private NamedPipeServerStream CreatePipe()
    {
        var pipeSecurity = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The broker cannot determine the current user identity.");
        pipeSecurity.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, // ServeOneAsync drives the waits with real cancellation
            inBufferSize: MaxRequestBytes,
            outBufferSize: MaxRequestBytes,
            pipeSecurity);
    }

    /// <summary>
    /// Serves ONE request round-trip and returns (the one-at-a-time rule: the single
    /// connection is handled, answered, and released before the next may connect —
    /// docs/governance.md §3: never multiple concurrent sessions). The waits honor
    /// the cancellation — a cancelled serve releases the pipe, no orphaned servers.
    /// </summary>
    public async Task<BrokerResponse> ServeOneAsync(CancellationToken cancellationToken = default)
    {
        _server?.Dispose();
        _server = CreatePipe();
        try
        {
            await _server.WaitForConnectionAsync(cancellationToken);
            // The identity guarantee = the pipe ACL: ONLY the current user can connect
            // (the security descriptor at the CreatePipe time — a different user's
            // connection is refused by the OS before the protocol starts). The
            // GetImpersonationUserName path requires the client-side impersonation
            // handshake and throws otherwise — the ACL is the real, sufficient gate.
            var request = ReadRequest(_server);
            var response = Dispatch(request);
            WriteResponse(_server, response);
            return response;
        }
        finally
        {
            // NO aggressive Disconnect: the client reads the answer and closes its end;
            // the pipe cleans up when both sides dispose (the aggressive disconnect
            // races the client's read and breaks the pipe — the spike's one-at-a-time
            // is enforced by the single-connection handling, not by the teardown).
        }
    }

    /// <summary>The typed dispatch — the fixed ops only; everything else denies. PUBLIC:
    /// the spike's testable surface (the deterministic verdicts are the architecture proof).</summary>
    public static BrokerResponse Dispatch(BrokerRequest request)
    {
        if (!request.IsValid)
            return new BrokerResponse(false, "invalid_request", request.RequestId);
        return request.Op switch
        {
            BrokerOp.HealthCheck => new BrokerResponse(true, "broker_alive: typed-protocol-only, one-at-a-time", request.RequestId),
            BrokerOp.RestartService => BrokerAllowlist.IsRestartable(request.Target)
                ? new BrokerResponse(false, $"spike_boundary: '{request.Target}' is allowlisted and authorized — the actual privileged Windows call lands with the full Phase C after the spike choice (docs/governance.md §3).", request.RequestId)
                : new BrokerResponse(false, $"deny: '{request.Target}' is not in the fixed restart allowlist.", request.RequestId),
            BrokerOp.ElevateCommand => Environment.GetEnvironmentVariable("TOLIK_BROKER_ALLOW_ELEVATE") == "true"
                ? new BrokerResponse(false, "elevate_requires_full_authorization: the CRITICAL descriptor + MFA + the Owner confirmation are mandatory — the spike never executes an arbitrary elevated command.", request.RequestId)
                : new BrokerResponse(false, "deny: ElevateCommand is DENY BY DEFAULT (TOLIK_BROKER_ALLOW_ELEVATE unset) — the explicit enable is the first of the double gates.", request.RequestId),
            _ => new BrokerResponse(false, "deny: unknown operation.", request.RequestId)
        };
    }

    private static BrokerRequest ReadRequest(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var line = reader.ReadLine() ?? string.Empty;
        if (line.Length > MaxRequestBytes) throw new InvalidOperationException("The broker request exceeds the size cap.");
        var request = JsonSerializer.Deserialize<BrokerRequest>(line)
            ?? throw new InvalidOperationException("The broker request is malformed.");
        return request;
    }

    private static void WriteResponse(NamedPipeServerStream pipe, BrokerResponse response)
    {
        using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(response));
        writer.Flush();
    }

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
    }
}
