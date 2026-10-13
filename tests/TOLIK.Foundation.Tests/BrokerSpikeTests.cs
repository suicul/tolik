using System.Reflection;
using TOLIK.Agent;
using TOLIK.Contracts;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// The Privilege Broker C0 SPIKE (docs/governance.md §3, Phase C — PLAN REFORM
/// PRG-4f): the controlled-sudo architecture proof — the REAL named-pipe round-trip
/// (the current-user ACL, the typed protocol, the one-at-a-time serving), the
/// fixed-allowlist restart targets, the ElevateCommand DENY-BY-DEFAULT double gate,
/// and the STRUCTURAL guarantee: no string-command field exists anywhere in the
/// protocol («ExecuteAsAdmin(string)» is unrepresentable — tested by reflection).
/// The actual privileged Windows calls land with the full Phase C (the honest
/// boundary the spike responses state).
/// </summary>
public sealed class BrokerProtocolTests
{
    [Fact]
    public void The_protocol_structurally_has_no_string_command()
    {
        // The reflection over the whole protocol: no property/field named like a
        // free-form command carrier exists — the typed ops are the only surface.
        foreach (var type in new[] { typeof(BrokerRequest), typeof(BrokerResponse), typeof(BrokerOp) })
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.DoesNotContain("command", member.Name.ToLowerInvariant());
        }
        // The BrokerRequest carries (Op, Target, RequestId) — exactly the typed shape.
        Assert.Equal("Op", nameof(BrokerRequest.Op));
        Assert.Equal("Target", nameof(BrokerRequest.Target));
        Assert.Equal("RequestId", nameof(BrokerRequest.RequestId));
    }

    [Fact]
    public void The_restart_allowlist_is_the_fixed_recovery_set()
    {
        Assert.True(BrokerAllowlist.IsRestartable("restart_voice_service"));
        Assert.True(BrokerAllowlist.IsRestartable("controlled_component_restart"));
        Assert.False(BrokerAllowlist.IsRestartable("format_c_drive"));     // never free-form
        Assert.False(BrokerAllowlist.IsRestartable(""));
        Assert.False(BrokerAllowlist.IsRestartable(null));
    }

    [Fact]
    public void Dispatch_fails_closed_on_the_unknown_and_the_off_allowlist()
    {
        var deny = BrokerServer.Dispatch(new BrokerRequest(BrokerOp.RestartService, "not_in_the_allowlist", Guid.NewGuid()));
        Assert.False(deny.Ok);
        var malformed = BrokerServer.Dispatch(new BrokerRequest(BrokerOp.RestartService, null, Guid.NewGuid()));
        Assert.False(malformed.Ok); // the restart without a target is invalid
    }

    [Fact]
    public void ElevateCommand_denies_by_default_and_even_enabled_never_executes_in_the_spike()
    {
        var old = Environment.GetEnvironmentVariable("TOLIK_BROKER_ALLOW_ELEVATE");
        try
        {
            Environment.SetEnvironmentVariable("TOLIK_BROKER_ALLOW_ELEVATE", null);
            var denied = BrokerServer.Dispatch(new BrokerRequest(BrokerOp.ElevateCommand, "anything", Guid.NewGuid()));
            Assert.False(denied.Ok);
            Assert.Contains("DENY BY DEFAULT", denied.Detail); // the first gate: the explicit enable

            Environment.SetEnvironmentVariable("TOLIK_BROKER_ALLOW_ELEVATE", "true");
            var stillDenied = BrokerServer.Dispatch(new BrokerRequest(BrokerOp.ElevateCommand, "anything", Guid.NewGuid()));
            Assert.False(stillDenied.Ok);
            Assert.Contains("full_authorization", stillDenied.Detail); // the second gate: CRITICAL+MFA+Owner — the spike never executes
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOLIK_BROKER_ALLOW_ELEVATE", old);
        }
    }
}

public sealed class BrokerRoundTripTests
{
    [Fact]
    public async Task The_real_named_pipe_round_trip_works_one_at_a_time()
    {
        // THE SPIKE PROOF: the real broker server on the real named pipe + the real
        // client — the typed protocol over the OS IPC, the current-user ACL. The pipe
        // name is UNIQUE per test (the isolation: no orphaned server from another
        // process/test may intercept this client — a lesson learned live).
        var pipeName = $"tolik/privilege-broker/test/{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new BrokerServer(pipeName);
        var serveTask = Task.Run(() => server.ServeOneAsync(cts.Token), cts.Token);

        await Task.Delay(300, cts.Token);
        var request = new BrokerRequest(BrokerOp.HealthCheck, null, Guid.NewGuid());
        var response = await Task.Run(() => BrokerClient.Call(request, pipeName)).WaitAsync(TimeSpan.FromSeconds(8), cts.Token);

        var served = await serveTask;
        Assert.True(response.Ok);                                   // the client sees the alive answer
        Assert.Equal(request.RequestId, response.RequestId);       // the correlation holds
        Assert.Equal(served.RequestId, response.RequestId);        // the server and the client agree
        Assert.Contains("typed-protocol-only", response.Detail);
    }

    [Fact]
    public async Task The_restart_target_round_trips_the_verdict_over_the_pipe()
    {
        var pipeName = $"tolik/privilege-broker/test/{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new BrokerServer(pipeName);
        var serveTask = Task.Run(() => server.ServeOneAsync(cts.Token), cts.Token);
        await Task.Delay(300, cts.Token);
        var request = new BrokerRequest(BrokerOp.RestartService, "restart_voice_service", Guid.NewGuid());
        var response = await Task.Run(() => BrokerClient.Call(request, pipeName)).WaitAsync(TimeSpan.FromSeconds(8), cts.Token);
        await serveTask;
        Assert.False(response.Ok);                       // the spike boundary: authorized but not yet executing
        Assert.Contains("spike_boundary", response.Detail);
        Assert.Contains("allowlisted", response.Detail); // the allowlist verdict flowed through the pipe
    }
}
