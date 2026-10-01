using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using TOLIK.Infrastructure.Persistence;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Core;
using TOLIK.Contracts;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Creates an isolated Core host per test: fresh PostgreSQL database, fresh host,
/// zero shared state. Uses UseSetting (host configuration), which minimal hosting
/// merges BEFORE Program.cs reads the configuration — ConfigureAppConfiguration is
/// applied too late for AddTolikPostgres's startup-time connection-string read.
/// </summary>
public sealed class CoreHostFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "tolik_test_" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting flows into WebApplicationBuilder's initial configuration.
        builder.UseSetting("ConnectionStrings:Postgres", $"Host=localhost;Port=5432;Database={_databaseName};Username=tolik;Password=TolikSecureP@ss2026!");
        builder.UseSetting("TOLIK_BOOTSTRAP_CODE", "TLK-BSTEP-2026-INIT");
        builder.UseSetting("TOLIK_OFFLINE_MODE", "true");
        builder.UseSetting("TOLIK_ALLOWED_ROOTS", "C:\\DEV");
        builder.UseSetting("TOLIK_VOICE:COSYVOICE_ENDPOINT", "http://localhost:8000");

        builder.ConfigureServices(services =>
        {
            // Test doubles: no external TTS dependency in integration tests.
            services.RemoveAll<CosyVoice3TtsProvider>();
            services.RemoveAll<ITtsProvider>();
            services.RemoveAll<ILocalVoiceRuntime>();
            services.AddSingleton<ITtsProvider, TestTtsProvider>();
            services.AddSingleton<ILocalVoiceRuntime, TestTtsProvider>();

            // Register test doubles for new background services
            services.RemoveAll<ProactiveReminderBackgroundService>();
            services.AddSingleton<ProactiveReminderBackgroundService>();
            services.RemoveAll<ProactiveStore>();
            services.AddSingleton<ProactiveStore>();
            services.RemoveAll<ConfirmationStore>();
            services.AddSingleton<ConfirmationStore>();
            services.RemoveAll<RealtimeEventHub>();
            services.AddSingleton<RealtimeEventHub>();
        });
    }

    /// <summary>
    /// No CreateHost override: Program.cs auto-migration creates the unique test database
    /// on first boot (MigrateAsync on a missing database) — the test host is built exactly
    /// like production, via the base-class deferred-host flow.
    /// </summary>
}

/// <summary>Test TTS provider that returns empty audio (disabled TTS for testing).</summary>
public sealed class TestTtsProvider : ITtsProvider, ILocalVoiceRuntime
{
    public Task<TtsAudio> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new TtsAudio(Array.Empty<byte>(), "audio/wav", TimeSpan.Zero));

    public Task<CosyVoiceHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new CosyVoiceHealth(true, "TestTTS", DateTimeOffset.UtcNow));
}

/// <summary>Shared enrollment helper: full bootstrap challenge + P-256 DER enroll.</summary>
internal static class TestEnrollment
{
    public static async Task<string> EnrollOwnerAsync(HttpClient client)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();

        var challengeResponse = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = "TLK-BSTEP-2026-INIT" });
        if (challengeResponse.StatusCode != HttpStatusCode.OK)
        {
            var errorBody = await challengeResponse.Content.ReadAsStringAsync();
            Assert.Fail($"Challenge failed: {(int)challengeResponse.StatusCode} — body: {errorBody}");
        }
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>();
        Assert.NotNull(challenge);

        var signature = Convert.ToBase64String(key.SignData(
            System.Text.Encoding.UTF8.GetBytes(challenge!.challenge),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        var enrollResponse = await client.PostAsJsonAsync("/auth/bootstrap/enroll", new
        {
            bootstrapCode = "TLK-BSTEP-2026-INIT",
            installationId = "test-install-" + Guid.NewGuid().ToString("N")[..8],
            publicKey = publicKey,
            challenge = challenge.challenge,
            signature = signature
        });
        if (enrollResponse.StatusCode != HttpStatusCode.OK)
        {
            var errorBody = await enrollResponse.Content.ReadAsStringAsync();
            Assert.Fail($"Enroll failed: {(int)enrollResponse.StatusCode} — body: {errorBody}");
        }
        var enrolled = await enrollResponse.Content.ReadFromJsonAsync<EnrollResponse>();
        Assert.NotNull(enrolled?.sessionToken);
        return enrolled!.sessionToken!;
    }
}

public sealed class BootstrapIntegrationTests
{
    [Fact]
    public async Task Bootstrap_Challenge_Returns_Token()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = "TLK-BSTEP-2026-INIT" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChallengeResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.challenge);
        Assert.True(body.expiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Bootstrap_Enroll_With_Valid_P256_Key_Returns_Session()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        await TestEnrollment.EnrollOwnerAsync(client);
        // Assert inside the helper already verifies the session token.
    }

    [Fact]
    public async Task Bootstrap_Enroll_Rejects_Invalid_Signature()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var challengeResponse = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = "TLK-BSTEP-2026-INIT" });
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>();
        Assert.NotNull(challenge);

        var enrollResponse = await client.PostAsJsonAsync("/auth/bootstrap/enroll", new
        {
            bootstrapCode = "TLK-BSTEP-2026-INIT",
            installationId = "test-install-" + Guid.NewGuid().ToString("N")[..8],
            publicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q8X7Q==",
            challenge = challenge!.challenge,
            signature = "invalid_signature"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, enrollResponse.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_Second_Enroll_Is_Rejected()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        await TestEnrollment.EnrollOwnerAsync(client);

        // The bootstrap row is consumed — a second owner enrollment must fail (§13: single owner).
        var second = await client.PostAsJsonAsync("/auth/bootstrap/challenge", new { bootstrapCode = "TLK-BSTEP-2026-INIT" });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task LocalToken_Is_Disabled_By_Default()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/auth/local-token");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LocalToken_Mints_Working_Owner_Session_When_Enabled()
    {
        using var factory = new CoreHostFactory().WithWebHostBuilder(b => b.UseSetting("TOLIK_DEV_AUTO_TOKEN", "true"));
        using var client = factory.CreateClient();

        await TestEnrollment.EnrollOwnerAsync(client);

        var response = await client.GetAsync("/auth/local-token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<LocalTokenResponse>();
        Assert.False(string.IsNullOrWhiteSpace(token!.sessionToken));

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.sessionToken);
        var agents = await client.GetAsync("/agents");
        Assert.Equal(HttpStatusCode.OK, agents.StatusCode);
    }
}

public sealed class AgentIntegrationTests
{
    [Fact]
    public async Task Agent_Can_Connect_And_Execute_RunProcess()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        // Connect agent
        var agentId = Guid.NewGuid();
        var connectResponse = await client.PostAsJsonAsync("/agents/connect", new
        {
            agentId = agentId,
            advertisedCapabilities = new[] { "runProcess", "readFile", "listDirectory", "getProcessStatus" }
        });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);
        var connected = await connectResponse.Content.ReadFromJsonAsync<ConnectResponse>();
        Assert.True(connected!.connected);

        // Simulated agent poll loop — mirrors WindowsAgent.PollCommandAsync/CompleteCommandAsync.
        var enumOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var agentLoop = Task.Run(async () =>
        {
            while (!testCts.IsCancellationRequested)
            {
                try
                {
                    var pollResponse = await client.GetAsync($"/agents/{agentId}/commands/poll", testCts.Token);
                    if (pollResponse.StatusCode == HttpStatusCode.NoContent)
                    {
                        await Task.Delay(200, testCts.Token);
                        continue;
                    }
                    if (!pollResponse.IsSuccessStatusCode) break;
                    var command = await pollResponse.Content.ReadFromJsonAsync<AgentCommandEnvelope>(enumOptions, testCts.Token);
                    if (command is null) break;
                    var completion = new AgentCommandResponse(command.CommandId, command.AgentId, CommandOutcome.Completed, "exitCode=0;stdout=;stderr=;note=test_agent", null);
                    var completeResponse = await client.PostAsJsonAsync($"/agents/{agentId}/commands/complete", completion, enumOptions, testCts.Token);
                    if (!completeResponse.IsSuccessStatusCode) break;
                    return;
                }
                catch (OperationCanceledException) { break; }
            }
        }, testCts.Token);

        // Task with run_process step
        var taskResponse = await client.PostAsJsonAsync("/tasks", new { description = "Open calculator" });
        Assert.Equal(HttpStatusCode.Accepted, taskResponse.StatusCode);
        var task = await taskResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(task);

        var planResponse = await client.PostAsJsonAsync($"/tasks/{task!.id}/plan", new { request = "run_process:calc.exe;", maxSteps = 1, budgetSeconds = 30 });
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);

        var executeResponse = await client.PostAsJsonAsync($"/tasks/{task.id}/execute", new { request = "run_process:calc.exe;", maxSteps = 1, budgetSeconds = 30 });
        Assert.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        await agentLoop;

        var executed = await executeResponse.Content.ReadFromJsonAsync<ExecuteResponse>();
        Assert.NotNull(executed);
        Assert.Single(executed!.results);
        Assert.Contains("exitCode", executed.results[0]);
    }

    [Fact]
    public async Task Agent_Can_Execute_RunBuild_Task_Step()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        // Connect agent advertising RunBuild
        var agentId = Guid.NewGuid();
        var connectResponse = await client.PostAsJsonAsync("/agents/connect", new
        {
            agentId = agentId,
            advertisedCapabilities = new[] { "runBuild", "runTests" }
        });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);

        // Simulated agent poll loop — completes any command like the real agent.
        var enumOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var agentLoop = Task.Run(async () =>
        {
            while (!testCts.IsCancellationRequested)
            {
                try
                {
                    var pollResponse = await client.GetAsync($"/agents/{agentId}/commands/poll", testCts.Token);
                    if (pollResponse.StatusCode == HttpStatusCode.NoContent)
                    {
                        await Task.Delay(200, testCts.Token);
                        continue;
                    }
                    if (!pollResponse.IsSuccessStatusCode) break;
                    var command = await pollResponse.Content.ReadFromJsonAsync<AgentCommandEnvelope>(enumOptions, testCts.Token);
                    if (command is null) break;
                    Assert.Equal(AgentCapability.RunBuild, command.Capability);
                    var completion = new AgentCommandResponse(command.CommandId, command.AgentId, CommandOutcome.Completed, "exitCode=0;stdout=;stderr=;note=test_agent", null);
                    var completeResponse = await client.PostAsJsonAsync($"/agents/{agentId}/commands/complete", completion, enumOptions, testCts.Token);
                    if (!completeResponse.IsSuccessStatusCode) break;
                    return;
                }
                catch (OperationCanceledException) { break; }
            }
        }, testCts.Token);

        // Task with run_build step (self-improvement gate: build must pass)
        var taskResponse = await client.PostAsJsonAsync("/tasks", new { description = "Build project" });
        Assert.Equal(HttpStatusCode.Accepted, taskResponse.StatusCode);
        var task = await taskResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(task);

        var planResponse = await client.PostAsJsonAsync($"/tasks/{task!.id}/plan", new { request = "run_build:C:\\DEV;", maxSteps = 1, budgetSeconds = 30 });
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);

        var executeResponse = await client.PostAsJsonAsync($"/tasks/{task.id}/execute", new { request = "run_build:C:\\DEV;", maxSteps = 1, budgetSeconds = 30 });
        Assert.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        await agentLoop;

        var executed = await executeResponse.Content.ReadFromJsonAsync<ExecuteResponse>();
        Assert.NotNull(executed);
        Assert.Single(executed!.results);
        Assert.Contains("exitCode", executed.results[0]);
    }

    [Fact]
    public async Task Feedback_Identical_Comments_Strengthen_Single_Rule()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var taskResponse = await client.PostAsJsonAsync("/tasks", new { description = "Learning loop test" });
        Assert.Equal(HttpStatusCode.Accepted, taskResponse.StatusCode);
        var task = await taskResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(task);

        // Same explicit feedback twice — the rule must be strengthened, not duplicated (§21).
        const string comment = "после правок всегда запускай сборку";
        var first = await client.PostAsJsonAsync($"/tasks/{task!.id}/feedback", new { positive = true, comment = comment });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await client.PostAsJsonAsync($"/tasks/{task.id}/feedback", new { positive = true, comment = comment });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var rulesResponse = await client.GetAsync("/rules");
        Assert.Equal(HttpStatusCode.OK, rulesResponse.StatusCode);
        var rules = await rulesResponse.Content.ReadFromJsonAsync<List<LearnedRule>>();
        Assert.NotNull(rules);
        var matches = rules!.Where(r => r.Rule.Contains(comment, StringComparison.Ordinal)).ToArray();
        Assert.Single(matches);
        Assert.Equal(0.7, matches[0].Confidence, precision: 5);
    }
}

public sealed class MemoryIntegrationTests
{
    [Fact]
    public async Task Owner_can_delete_own_memory_and_second_delete_is_not_found()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var marker = "удаляемый факт " + Guid.NewGuid().ToString("N");
        var createResponse = await client.PostAsJsonAsync("/memory", new
        {
            scope = "Private",
            content = marker,
            source = "test",
            confidence = 0.9
        });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<MemoryApiItem>();
        Assert.NotNull(created);

        var beforeDelete = await (await client.GetAsync("/memory/search?q=" + Uri.EscapeDataString(marker))).Content.ReadAsStringAsync();
        Assert.Contains(marker, beforeDelete, StringComparison.Ordinal);

        var deleteResponse = await client.DeleteAsync($"/memory/{created!.id:D}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var afterDelete = await (await client.GetAsync("/memory/search?q=" + Uri.EscapeDataString(marker))).Content.ReadAsStringAsync();
        Assert.DoesNotContain(marker, afterDelete, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/memory/{created.id:D}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/memory/{Guid.NewGuid():D}")).StatusCode);
    }

    [Fact]
    public async Task Delete_memory_without_token_is_rejected()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/memory/{Guid.NewGuid():D}")).StatusCode);
    }

    [Fact]
    public async Task Development_can_reset_owner_memory()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableMemoryStore>();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();

        await store.AddAsync(owner, MemoryScope.Private, "a", "test", 0.9, origin: MemoryOrigin.Lived);
        await store.AddAsync(owner, MemoryScope.Shared, "b", "test", 0.9, origin: MemoryOrigin.Imported);
        await store.AddAsync(other, MemoryScope.Private, "c", "test", 0.9, origin: MemoryOrigin.Lived);

        Assert.Equal(2, await store.DeleteAllAsync(owner));
        Assert.Empty(await store.SearchAsync(owner, "a"));
        Assert.Empty(await store.SearchAsync(owner, "b"));
        Assert.Single(await store.SearchAsync(other, "c"));
        Assert.Equal(0, await store.DeleteAllAsync(owner));
    }

    [Fact]
    public async Task Memory_origin_round_trips_and_bad_origin_rejected()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var marker = "origin факт " + Guid.NewGuid().ToString("N");
        var imported = await client.PostAsJsonAsync("/memory", new
        {
            scope = "Private",
            content = marker,
            source = "test",
            confidence = 0.9,
            origin = "imported"
        });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var created = await imported.Content.ReadFromJsonAsync<MemoryApiItem>();
        Assert.Equal("imported", created!.origin);

        var defaulted = await client.PostAsJsonAsync("/memory", new
        {
            scope = "Private",
            content = marker + " lived",
            source = "test",
            confidence = 0.9
        });
        Assert.Equal(HttpStatusCode.OK, defaulted.StatusCode);
        var createdDefault = await defaulted.Content.ReadFromJsonAsync<MemoryApiItem>();
        Assert.Equal("lived", createdDefault!.origin);

        var bad = await client.PostAsJsonAsync("/memory", new
        {
            scope = "Private",
            content = marker,
            source = "test",
            confidence = 0.9,
            origin = "nope"
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

public sealed class MultiStepIntegrationTests
{
    [Fact]
    public async Task Execute_runs_structured_steps_in_order()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var agentId = Guid.NewGuid();
        var connectResponse = await client.PostAsJsonAsync("/agents/connect", new
        {
            agentId = agentId,
            advertisedCapabilities = new[] { "runBuild", "runTests" }
        });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);

        // Simulated agent serving every polled command (mirrors the real agent loop).
        var enumOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var served = new ConcurrentQueue<AgentCapability>();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var agentLoop = Task.Run(async () =>
        {
            while (!testCts.IsCancellationRequested)
            {
                try
                {
                    var pollResponse = await client.GetAsync($"/agents/{agentId}/commands/poll", testCts.Token);
                    if (pollResponse.StatusCode == HttpStatusCode.NoContent)
                    {
                        await Task.Delay(200, testCts.Token);
                        continue;
                    }
                    if (!pollResponse.IsSuccessStatusCode) break;
                    var command = await pollResponse.Content.ReadFromJsonAsync<AgentCommandEnvelope>(enumOptions, testCts.Token);
                    if (command is null) break;
                    served.Enqueue(command.Capability);
                    var completion = new AgentCommandResponse(command.CommandId, command.AgentId, CommandOutcome.Completed, "exitCode=0;stdout=;stderr=;note=test_agent", null);
                    var completeResponse = await client.PostAsJsonAsync($"/agents/{agentId}/commands/complete", completion, enumOptions, testCts.Token);
                    if (!completeResponse.IsSuccessStatusCode) break;
                }
                catch (OperationCanceledException) { break; }
            }
        }, testCts.Token);

        var taskResponse = await client.PostAsJsonAsync("/tasks", new { description = "Build then test" });
        Assert.Equal(HttpStatusCode.Accepted, taskResponse.StatusCode);
        var task = await taskResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(task);

        var planResponse = await client.PostAsJsonAsync($"/tasks/{task!.id}/plan",
            new { request = "Build then test", maxSteps = 2, budgetSeconds = 30, steps = new[] { "run_build:C:\\DEV;", "run_tests:C:\\DEV;" } });
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);

        var executeResponse = await client.PostAsJsonAsync($"/tasks/{task.id}/execute",
            new { request = "Build then test", maxSteps = 2, budgetSeconds = 30, steps = new[] { "run_build:C:\\DEV;", "run_tests:C:\\DEV;" } });
        Assert.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        var executed = await executeResponse.Content.ReadFromJsonAsync<ExecuteResponse>();
        Assert.NotNull(executed);
        Assert.Equal(2, executed!.results.Count);
        Assert.Equal(new[] { AgentCapability.RunBuild, AgentCapability.RunTests }, served.ToArray());

        testCts.Cancel();
        await agentLoop;
    }

    [Fact]
    public async Task Failed_step_stops_plan_and_returns_partial_results()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var agentId = Guid.NewGuid();
        var connectResponse = await client.PostAsJsonAsync("/agents/connect", new
        {
            agentId = agentId,
            advertisedCapabilities = new[] { "runBuild", "runTests" }
        });
        Assert.Equal(HttpStatusCode.OK, connectResponse.StatusCode);

        var enumOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var served = new ConcurrentQueue<AgentCapability>();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var agentLoop = Task.Run(async () =>
        {
            while (!testCts.IsCancellationRequested)
            {
                try
                {
                    var pollResponse = await client.GetAsync($"/agents/{agentId}/commands/poll", testCts.Token);
                    if (pollResponse.StatusCode == HttpStatusCode.NoContent)
                    {
                        await Task.Delay(200, testCts.Token);
                        continue;
                    }
                    if (!pollResponse.IsSuccessStatusCode) break;
                    var command = await pollResponse.Content.ReadFromJsonAsync<AgentCommandEnvelope>(enumOptions, testCts.Token);
                    if (command is null) break;
                    served.Enqueue(command.Capability);
                    // Second step fails on the agent: build ok, tests broken.
                    var outcome = command.Capability == AgentCapability.RunTests ? CommandOutcome.Failed : CommandOutcome.Completed;
                    var completion = new AgentCommandResponse(command.CommandId, command.AgentId, outcome,
                        outcome == CommandOutcome.Completed ? "exitCode=0;note=test_agent" : null,
                        outcome == CommandOutcome.Completed ? null : "exitCode=1;stderr=boom");
                    var completeResponse = await client.PostAsJsonAsync($"/agents/{agentId}/commands/complete", completion, enumOptions, testCts.Token);
                    if (!completeResponse.IsSuccessStatusCode) break;
                }
                catch (OperationCanceledException) { break; }
            }
        }, testCts.Token);

        var taskResponse = await client.PostAsJsonAsync("/tasks", new { description = "Build then broken tests" });
        var task = await taskResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(task);

        var executeResponse = await client.PostAsJsonAsync($"/tasks/{task!.id}/execute",
            new { request = "probe", maxSteps = 2, budgetSeconds = 30, steps = new[] { "run_build:C:\\DEV;", "run_tests:C:\\DEV;" } });
        Assert.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        var body = await executeResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.GetProperty("failed").GetBoolean());
        Assert.Single(body.GetProperty("results").EnumerateArray());
        Assert.Equal(new[] { AgentCapability.RunBuild, AgentCapability.RunTests }, served.ToArray());

        testCts.Cancel();
        await agentLoop;
    }

    [Fact]
    public async Task Unknown_action_still_returns_forbidden()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var taskResponse = await client.PostAsJsonAsync("/tasks", new { description = "Bad action" });
        var task = await taskResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(task);

        var executeResponse = await client.PostAsJsonAsync($"/tasks/{task!.id}/execute",
            new { request = "probe", maxSteps = 1, budgetSeconds = 30, steps = new[] { "delete_everything:now" } });
        Assert.Equal(HttpStatusCode.Forbidden, executeResponse.StatusCode);
    }
}

public sealed class AuditIntegrationTests
{
    [Fact]
    public async Task Owner_reads_own_audit_trail()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var response = await client.GetAsync("/audit?take=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<AuditApiItem>>();
        Assert.NotNull(events);
        Assert.NotEmpty(events!);
        Assert.All(events!, e => Assert.False(string.IsNullOrWhiteSpace(e.eventType)));
    }

    [Fact]
    public async Task Audit_rejects_anonymous_and_bad_pagination()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/audit")).StatusCode);

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/audit?take=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/audit?take=101")).StatusCode);
    }
}

public sealed class StrictJsonBoundaryTests
{
    [Fact]
    public async Task Memory_rejects_unknown_field_and_accepts_control()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        var marker = "strict факт " + Guid.NewGuid().ToString("N");

        // Control: same payload without the extra prop still succeeds.
        var control = await client.PostAsJsonAsync("/memory", new
        {
            scope = "Private",
            content = marker,
            source = "test",
            confidence = 0.9
        });
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);

        // Unknown field → 400 (UnmappedMemberHandling.Disallow at the HTTP boundary).
        var bad = await client.PostAsJsonAsync("/memory", new
        {
            scope = "Private",
            content = marker,
            source = "test",
            confidence = 0.9,
            injectedField = "should_be_rejected"
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Tasks_rejects_unknown_field_and_accepts_control()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();

        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        // Control: declared-only payload is accepted (202).
        var control = await client.PostAsJsonAsync("/tasks", new { description = "strict boundary probe" });
        Assert.Equal(HttpStatusCode.Accepted, control.StatusCode);

        // Unknown field → 400.
        var bad = await client.PostAsJsonAsync("/tasks", new
        {
            description = "strict boundary probe",
            injectedField = "should_be_rejected"
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

// Response DTOs
file sealed record ChallengeResponse(string challenge, DateTimeOffset expiresAt);
file sealed record LocalTokenResponse(string sessionToken, Guid sessionId, DateTimeOffset expiresAt);
file sealed record EnrollResponse(Guid userId, Guid deviceId, Guid sessionId, string sessionToken);
file sealed record ConnectResponse(bool connected, Guid agentId, string[] capabilities);
file sealed record TaskResponse(Guid id, string description, string status);
file sealed record ExecuteResponse(Guid taskId, List<string> results);
file sealed record MemoryApiItem(Guid id, Guid ownerId, string scope, string content, string source, double confidence, DateTimeOffset createdAt, string? origin);
file sealed record AuditApiItem(Guid id, string eventType, string decision, string reason, string? userId, DateTimeOffset occurredAt);
