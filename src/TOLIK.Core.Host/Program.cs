using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using TOLIK.Core;
using TOLIK.Infrastructure;
using TOLIK.Infrastructure.Logging;
using TOLIK.Infrastructure.Persistence;
using TOLIK.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using TOLIK.Infrastructure.Auditing;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Tasks;
using TOLIK.Contracts;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Text.Json.Serialization;
using TOLIK.Infrastructure.Models;
using TOLIK.Core.Host;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddTolikSecurityLogging();
// Per-query EF logs at Information level spam the console on every agent
// heartbeat/poll (several JSON lines per 2s tick). Warnings+ stay.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
// Configure WebSocket keep-alive interval (30s)
builder.Services.Configure<Microsoft.AspNetCore.Builder.WebSocketOptions>(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
});
builder.Services.AddTolikCoreInfrastructure();
builder.Services.AddTolikPostgres(builder.Configuration);
builder.Services.AddSingleton<ModelRouter>();
builder.Services.AddSingleton<TOLIK.Infrastructure.Authorization.AuthorizationLeaseStore>();
builder.Services.AddSingleton<OpenAiCompatibleRequestBuilder>();
builder.Services.AddHttpClient<ModelCompletionClient>();
// CosyVoiceOptions is a positional record — Configure<T> cannot instantiate it.
// Bind explicitly instead (§41: configuration over hardcoding).
builder.Services.AddSingleton(_ => new CosyVoiceOptions(
    Uri.TryCreate(builder.Configuration["TOLIK_VOICE:COSYVOICE_ENDPOINT"], UriKind.Absolute, out var voiceEndpoint) ? voiceEndpoint : new Uri("http://voice:9660"),
    TimeSpan.TryParse(builder.Configuration["TOLIK_VOICE:COSYVOICE_TIMEOUT"], out var voiceTimeout) ? voiceTimeout : TimeSpan.FromMinutes(1),
    int.TryParse(builder.Configuration["TOLIK_VOICE:COSYVOICE_MAX_AUDIO_BYTES"], out var voiceMaxBytes) ? voiceMaxBytes : 10_485_760));
builder.Services.AddHttpClient<CosyVoice3TtsProvider>();
builder.Services.Configure<WhisperOptions>(
    builder.Configuration.GetSection(WhisperOptions.SectionName));
builder.Services.AddHttpClient<WhisperSttProvider>(
    (sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<WhisperOptions>>().Value;
        if (opts.Endpoint is not null) client.BaseAddress = opts.Endpoint;
        client.Timeout = TimeSpan.FromMilliseconds(opts.TimeoutMs > 0 ? opts.TimeoutMs : 60_000);
    });
builder.Services.AddSingleton<MemoryCompactionService>();
builder.Services.AddHostedService<MemoryCompactionBackgroundService>();
builder.Services.AddHostedService<ProactiveReminderBackgroundService>();
builder.Services.AddHostedService<TaskWatchdogBackgroundService>();
builder.Services.AddSingleton<DailyReviewService>();
builder.Services.AddHostedService<DailyReviewBackgroundService>();
builder.Services.AddSingleton<VoiceRuntimeState>();
builder.Services.AddSingleton<SoundboardRateLimiter>();
builder.Services.AddSingleton<TOLIK.Infrastructure.Persistence.ProactiveStore>();
builder.Services.AddSingleton<TOLIK.Infrastructure.Persistence.LearnedRuleStore>();
builder.Services.AddSingleton<TOLIK.Infrastructure.Authorization.ConfirmationStore>();
builder.Services.AddSingleton<RealtimeEventHub>();
builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection("TOLIK_MODEL_OFFLINE"));
builder.Services.AddHttpClient<OllamaProvider>(
    (sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        client.BaseAddress = opts.Endpoint;
    });
builder.Services.AddSingleton<IVoiceService, DeterministicVoiceService>();
builder.Services.AddSingleton<VoiceProfile>(_ => new VoiceProfile("TOLIK Tactical Original", "ru-RU", -5, -.08, .72, .18, .28, VoiceMode.Tactical));
builder.Services.AddSingleton<ITtsProvider>(sp => sp.GetRequiredService<CosyVoice3TtsProvider>());
builder.Services.AddSingleton<ILocalVoiceRuntime>(sp => sp.GetRequiredService<CosyVoice3TtsProvider>());
builder.Services.AddSingleton<VoiceRuntimeGate>();
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddSingleton<AgentCommandDispatcher>();
builder.Services.AddSingleton<TaskLifecycle>();
builder.Services.AddSingleton<BoundedPlanner>();
builder.Services.AddSingleton<LlmTaskPlanner>();
builder.Services.AddSingleton<PlanExecutionGuard>();
builder.Services.AddSingleton<ScopedMemory>();
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var roots = configuration.GetSection("TOLIK_ALLOWED_ROOTS").Get<string[]>() ?? [];
    if (roots.Length == 0) throw new InvalidOperationException("TOLIK_ALLOWED_ROOTS must be configured before filesystem tools are enabled.");
    return new PathIsolationPolicy(roots);
});
builder.Services.AddSingleton<FileToolService>();
builder.Services.AddHealthChecks()
    .AddCheck<PostgresReadinessCheck>("postgres", tags: ["ready"]);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Use null naming policy to preserve PascalCase enum values (agent sends "RunProcess", not "runProcess")
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(null));
    options.SerializerOptions.Converters.Add(new ImmutableSetConverterFactory());
    // Bounded hardening: reject unknown JSON fields at the HTTP boundary (auto-400 on all POST DTOs).
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});

var app = builder.Build();
if (app.Configuration.GetValue<bool>("TOLIK_DEV_AUTO_TOKEN", false))
    app.Logger.LogWarning("TOLIK_DEV_AUTO_TOKEN is ON: same-machine UI may mint owner sessions. Never enable on shared/VPS hosts.");
var realtime = new RealtimeEventHub();
var soundboardLimiter = new SoundboardRateLimiter();

// Apply pending EF migrations on startup (В§30: never alter schema without a migration).
// Test databases created via EnsureCreated have tables but no __EFMigrationsHistory вЂ”
// those are skipped (Migrate would fail with "table already exists").
await ApplyPendingMigrationsAsync(app.Services);

app.Services.GetRequiredService<TaskLifecycle>().EventPublished += ev =>
    realtime.PublishToTaskOwner(ev.TaskId, System.Text.Json.JsonSerializer.Serialize(new { ev.TaskId, ev.Kind, ev.Detail, ev.OccurredAt }));
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Prototype UI changes often; never let browsers run stale JS/CSS.
    OnPrepareResponse = static ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-store";
    }
});
app.UseWebSockets();

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers.TryGetValue("X-Correlation-Id", out var supplied)
        && Guid.TryParse(supplied.FirstOrDefault(), out var parsed)
        ? parsed
        : Guid.NewGuid();
    context.Response.Headers["X-Correlation-Id"] = correlationId.ToString("D");
    context.Items["CorrelationId"] = correlationId;
    await next(context);
});

app.MapGet("/health", async (CoreRuntimeDescriptor runtime, HttpContext context, IConfiguration configuration, TOLIK.Infrastructure.Authorization.CoreSecurityState securityState) =>
{
    // Voice endpoint is configuration (§55: no baked-in hostnames for VPS moves).
    var voiceBase = configuration["TOLIK_VOICE:COSYVOICE_ENDPOINT"] ?? "http://voice:9660";
    bool voiceHealthy = false;
    try
    {
        using var http = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
        http.Timeout = TimeSpan.FromSeconds(3);
        var resp = await http.GetAsync(new Uri(new Uri(voiceBase.EndsWith('/') ? voiceBase : voiceBase + "/"), "health"));
        voiceHealthy = resp.IsSuccessStatusCode;
    }
    catch { }

    // Check agent connectivity
    bool agentHealthy = false;
    try
    {
        var registry = context.RequestServices.GetRequiredService<AgentConnectionRegistry>();
        // Access the connections dictionary via reflection
        var connField = typeof(AgentConnectionRegistry).GetField("connections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (connField != null)
        {
            var connections = connField.GetValue(registry);
            if (connections is System.Collections.IDictionary dict)
            {
                agentHealthy = dict.Count > 0;
            }
        }
    }
    catch { }

    // Check memory (database connectivity)
    bool memoryHealthy = false;
    try
    {
        var db = context.RequestServices.GetRequiredService<TOLIK.Infrastructure.Persistence.TolikDbContext>();
        memoryHealthy = await db.Database.CanConnectAsync();
    }
    catch { }

    bool coreHealthy = runtime.IsAuthorizationAuthority;

    return Results.Ok(new
    {
        core = coreHealthy,
        voice = voiceHealthy,
        agent = agentHealthy,
        memory = memoryHealthy,
        component = runtime.ComponentName,
        authority = runtime.IsAuthorizationAuthority ? "Core" : "None",
        execution = runtime.ExecutesOperatingSystemCommands ? "Local" : "DelegatedToAgent",
        locked = securityState.IsLocked
    });
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

/// <summary>
/// Applies pending EF migrations when the database was created through the migration
/// pipeline (has __EFMigrationsHistory) or is empty (first boot). Databases created
/// by EnsureCreated (integration tests) are left untouched.
/// </summary>
static async Task ApplyPendingMigrationsAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TOLIK.Infrastructure.Persistence.TolikDbContext>();
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrations");
    try
    {
        if (!await db.Database.CanConnectAsync())
        {
            // First boot: MigrateAsync creates the database and applies the full pipeline.
            logger.LogInformation("Database does not exist yet вЂ” creating schema via migrations (first boot).");
            await db.Database.MigrateAsync();
            logger.LogInformation("Database created and migrated.");
            return;
        }

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        bool historyExists, anyTableExists;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory')";
            historyExists = (bool)(await command.ExecuteScalarAsync() ?? false);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' LIMIT 1)";
            anyTableExists = (bool)(await command.ExecuteScalarAsync() ?? false);
        }

        if (anyTableExists && !historyExists)
        {
            // EnsureCreated databases (integration tests) own their schema.
            logger.LogInformation("Schema present without migration history (EnsureCreated database) вЂ” skipping Migrate.");
            return;
        }

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is up to date.");
            return;
        }
        logger.LogInformation("Applying {Count} pending migrations: {List}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync();
        logger.LogInformation("Migrations applied.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database migration failed on startup.");
        throw;
    }
}

static IResult AuthenticationRequired(HttpContext context)
{
    // Diagnostics only: whether a credential was presented at all (never the secret itself).
    var hasHeader = context.Request.Headers.TryGetValue("Authorization", out var values)
        && !string.IsNullOrWhiteSpace(values.FirstOrDefault());
    Console.WriteLine($"[AUTH] rejected {context.Request.Method} {context.Request.Path}, hasAuthHeader: {hasHeader}");
    return Results.Json(new
    {
        error = "authentication_required",
        correlationId = context.Items["CorrelationId"]
    }, statusCode: StatusCodes.Status401Unauthorized);
}

/// <summary>Maps common Russian application names to Windows executables (intent normalization).</summary>
static string ResolveAppAlias(string name) => name.Trim().ToLowerInvariant() switch
{
    "РєР°Р»cЊРєcѓР»cЏc‚РѕcЂ" or "calculator" => "calc.exe",
    "Р±Р»РѕРєРЅРѕc‚" or "notepad" => "notepad.exe",
    "РїРµР№РЅc‚" or "РїР°РёРЅc‚" or "paint" or "cЂРёcРѕРІР°Р»РєР°" => "mspaint.exe",
    "РїcЂРѕРІРѕРґРЅРёРє" or "explorer" or "c„Р°Р№Р»c‹" => "explorer.exe",
    "Р±cЂР°cѓР·РµcЂ" or "РёРЅc‚РµcЂРЅРµc‚" or "browser" => "msedge.exe",
    "c‚РµcЂРјРёРЅР°Р»" or "РєРѕРЅcРѕР»cЊ" or "cmd" or "РєРѕРјР°РЅРґРЅР°cЏ cc‚cЂРѕРєР°" => "cmd.exe",
    "powershell" or "РїcЂРѕРІРµcЂР°" => "powershell.exe",
    "РґРёcРїРµc‚cРµcЂ Р·Р°РґР°c" or "task manager" => "taskmgr.exe",
    "РЅР°cc‚cЂРѕР№РєРё" or "РїР°cЂР°РјРµc‚cЂc‹" or "settings" => "ms-settings:",
    _ => name
};

/// <summary>
/// Local-network check for the dev auto-token endpoint. Inside Docker the host's
/// loopback arrives NATed as a bridge/gateway address, so plain loopback matching
/// would reject legitimate same-machine callers. Loopback + RFC1918 is the
/// pragmatic boundary; the real gates remain the explicit opt-in flag and the
/// 127.0.0.1-only port publishing (never expose this identity on LAN/VPS).
/// </summary>
static bool IsLocalNetwork(System.Net.IPAddress address)
{
    if (System.Net.IPAddress.IsLoopback(address)) return true;
    // Kestrel reports Docker bridge peers as IPv4-mapped IPv6 (::ffff:172.19.0.1).
    if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
    var bytes = address.GetAddressBytes();
    return bytes.Length == 4 && (bytes[0] == 10
        || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        || (bytes[0] == 192 && bytes[1] == 168));
}
/// <summary>
/// Session lifetime from configuration (default: 7 days for personal use).
/// Revocation still works instantly via /auth/session/{id}/revoke.
/// </summary>
static TimeSpan GetSessionLifetime(HttpContext context)
{
    var config = context.RequestServices.GetRequiredService<IConfiguration>();
    var hours = config.GetValue("TOLIK_SESSION_LIFETIME_HOURS", 24 * 7);
    return hours is > 0 and <= 24 * 365 ? TimeSpan.FromHours(hours) : TimeSpan.FromDays(7);
}

/// <summary>
/// Handles voice-safe intents (OpenApp / SwitchVoiceMode / Reminder) inside /voice/turn
/// without requiring a model route. Returns null when the intent needs the model path.
/// </summary>
static async Task<IResult?> ProcessSimpleIntentAsync(
    SessionPrincipal principal,
    TOLIK.Core.ChatIntent intent,
    HttpContext context,
    DurableMemoryStore durableMemory,
    CancellationToken cancellationToken)
{
    switch (intent.Kind)
    {
        case ChatIntentKind.OpenApp:
        {
            var appName = intent.Argument.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(appName) || appName.Length > 256 || appName.Contains('\\') || appName.Contains('/'))
                return Results.Ok(new VoiceTurnResponse(appName, "Не понятно, какое приложение открыть.", VoiceMode.Normal, null, null, null));
            appName = ResolveAppAlias(appName);
            var dispatcher = new AgentCommandDispatcher(context.RequestServices.GetRequiredService<AgentConnectionRegistry>());
            try
            {
                await dispatcher.ExecuteAsync(principal.UserId, AgentCapability.RunProcess, appName, cancellationToken);
                await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK (voice): открыл '{appName}'", "voice.action.run_process", 1, cancellationToken);
                return await SpeakIntentResponse(context, $"Открыто: {appName}", intent.Argument);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                return await SpeakIntentResponse(context, "Не удалось открыть — агент недоступен.", intent.Argument);
            }
        }
        case ChatIntentKind.SwitchVoiceMode:
        {
            var runtimeState = context.RequestServices.GetRequiredService<VoiceRuntimeState>();
            return runtimeState.TrySwitch(intent.Argument, "voice_command")
                ? await SpeakIntentResponse(context, $"Голосовой режим: {intent.Argument}.", intent.Argument)
                : null;
        }
        case ChatIntentKind.StoreMemory:
        {
            var fact = intent.Argument.Trim();
            if (fact.Length == 0 || fact.Length > 8192) return null;
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, fact, "user_explicit", 1, cancellationToken);
            return await SpeakIntentResponse(context, "Запомнил: " + fact, fact);
        }
        case ChatIntentKind.StoreFamilyMeme:
        {
            var meme = intent.Argument.Trim();
            if (meme.Length == 0 || meme.Length > 8192) return null;
            var personal = context.RequestServices.GetRequiredService<PersonalContextStore>();
            await personal.AddHumorAsync(principal.UserId, MemoryScope.Shared, meme, "family_meme", cancellationToken);
            await personal.AddFamilyLoreAsync(principal.UserId, meme, cancellationToken);
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Shared, $"Семейный мем: {meme}", "family.meme", 1, cancellationToken);
            return await SpeakIntentResponse(context, "Отлично, это теперь наш семейный мем.", meme);
        }
        case ChatIntentKind.StoreFunny:
        {
            var personal = context.RequestServices.GetRequiredService<PersonalContextStore>();
            await personal.AddHumorAsync(principal.UserId, MemoryScope.Private, intent.Argument, "user_feedback", cancellationToken);
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"Было смешно: {intent.Argument}", "humor.funny", 0.9, cancellationToken);
            return await SpeakIntentResponse(context, "Ха, запомнил, это смешно.", intent.Argument);
        }
        case ChatIntentKind.RecallMemory:
        {
            var query = intent.Argument.Trim();
            if (query.Length == 0) return null;
            var found = await durableMemory.SearchAsync(principal.UserId, query, 5, cancellationToken);
            // Voice answers stay short: top-3 facts (dialogue logs are not facts).
            var facts = found.Where(m => m.Source is not ("chat.user" or "voice.user" or "chat.assistant" or "voice.assistant"))
                .Take(3)
                .Select(m => m.Content.Length > 120 ? m.Content[..120] + "вЂ¦" : m.Content)
                .ToList();
            var reply = facts.Count == 0
                ? $"Не помню ничего про {query}."
                : "Помню: " + string.Join(". Р Рµc‰c‘: ", facts);
            return await SpeakIntentResponse(context, reply, query);
        }
        case ChatIntentKind.Reminder:
        {
            var parts = intent.Argument.Split('|');
            if (parts.Length != 3) return null;
            if (!int.TryParse(parts[0], out var amount) || amount is < 1 or > 100000) return null;
            var unit = parts[1].ToLowerInvariant();
            var delay = unit.StartsWith('c') ? TimeSpan.FromSeconds(amount)
                : unit.StartsWith('c') ? TimeSpan.FromHours(amount)
                : TimeSpan.FromMinutes(amount);
            var reminderText = parts[2].Trim();
            var proactive = context.RequestServices.GetRequiredService<TOLIK.Infrastructure.Persistence.ProactiveStore>();
            await proactive.ScheduleAsync(principal.UserId, reminderText, "Normal", DateTimeOffset.UtcNow.Add(delay), "voice_reminder", cancellationToken);
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK (voice): напоминание {reminderText} через {delay}", "proactive.reminder", 1, cancellationToken);
            return await SpeakIntentResponse(context, $"Напомню через {delay.TotalMinutes:0.#} мин: {reminderText}", intent.Argument);
        }
    }
    return null;

    // Local helper: synth spoken confirmation so voice commands answer by voice (В§14).
    static async Task<IResult> SpeakIntentResponse(HttpContext context, string replyText, string transcription)
    {
        var gate = context.RequestServices.GetRequiredService<VoiceRuntimeGate>();
        var profile = context.RequestServices.GetRequiredService<VoiceProfile>();
        var runtimeState = context.RequestServices.GetRequiredService<VoiceRuntimeState>();
        var audio = await gate.SynthesizeIfEnabledAsync(new TtsRequest(replyText, profile, VoiceMode.Normal), runtimeState.Current, context.RequestAborted);
        return Results.Ok(new VoiceTurnResponse(transcription, replyText, VoiceMode.Normal, audio?.Data, audio?.ContentType, null));
    }
}
static async Task<SessionPrincipal?> AuthenticateRequestAsync(HttpContext context, IdentityService identity, CancellationToken cancellationToken)
{
    var header = context.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? await identity.AuthenticateAsync(header[7..], cancellationToken)
        : null;
}

/// <summary>
/// Resolves task steps (§47): explicit <c>Steps</c> win; <c>AutoPlan</c> asks the
/// LLM for a decomposition; anything else (including any model failure) falls
/// back to the deterministic single-step plan. Model output stays untrusted:
/// steps are sanitized/bounded here and authorized per-step at execute time.
/// </summary>
static async Task<IReadOnlyList<string>?> ResolveStepsAsync(PlanRequest request, string fallbackRequest, LlmTaskPlanner autoPlanner, ModelRouter router, ModelCompletionClient completion, CancellationToken cancellationToken)
{
    if (request.Steps is not null || !request.AutoPlan) return request.Steps;
    var route = router.ResolveWithFallback(ModelTaskKind.Simple);
    if (route is null) return null;
    return await autoPlanner.TryDecomposeAsync(
        request.Request ?? fallbackRequest, request.MaxSteps,
        (prompt, token) => completion.CompleteAsync(route, new ModelRequest(route.Model, prompt, NumPredict: 300, Think: false, NumCtx: 8192), TimeSpan.FromSeconds(30), token),
        cancellationToken);
}

app.MapPost("/auth/bootstrap/challenge", async (BootstrapChallengeRequest request, IdentityService identity, IConfiguration configuration, AuditWriter audit, HttpContext context, CancellationToken cancellationToken) =>
{
    var configured = configuration["TOLIK_BOOTSTRAP_CODE"];
    var correlationId = (Guid)context.Items["CorrelationId"]!;
    if (string.IsNullOrWhiteSpace(configured))
    {
        await audit.WriteAsync(correlationId, "auth.bootstrap.challenge", "DENY", "bootstrap_not_configured", cancellationToken);
        return Results.Json(new { error = "enrollment_denied", correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status401Unauthorized);
    }

    try
    {
        await identity.EnsureBootstrapAsync(configured, TimeSpan.FromMinutes(15), cancellationToken);
        var challenge = await identity.IssueDeviceChallengeAsync(request.BootstrapCode ?? string.Empty, TimeSpan.FromMinutes(5), cancellationToken);
        if (challenge is null)
        {
            await audit.WriteAsync(correlationId, "auth.bootstrap.challenge", "DENY", "challenge_denied", cancellationToken);
            return Results.Json(new { error = "enrollment_denied", correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status401Unauthorized);
        }
        await audit.WriteAsync(correlationId, "auth.bootstrap.challenge", "ALLOW", "challenge_issued", cancellationToken);
        return Results.Ok(new { challenge = challenge.Token, expiresAt = challenge.ExpiresAt });
    }
    catch (InvalidOperationException)
    {
        await audit.WriteAsync(correlationId, "auth.bootstrap.challenge", "DENY", "enrollment_denied", cancellationToken);
        return Results.Json(new { error = "enrollment_denied", correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        await audit.WriteAsync(correlationId, "auth.bootstrap.challenge", "DENY", "enrollment_unavailable", cancellationToken);
        // Never leak internals in production; in Development show the reason for diagnosis.
        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        if (environment.IsDevelopment())
        {
            var database = context.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");
            return Results.Json(new { error = "enrollment_unavailable", detail = ex.Message, inner = ex.InnerException?.Message, database, correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Json(new { error = "enrollment_unavailable", correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/auth/bootstrap/enroll", async (BootstrapRequest request, IdentityService identity, IConfiguration configuration, AuditWriter audit, HttpContext context, CancellationToken cancellationToken) =>
{
    var configured = configuration["TOLIK_BOOTSTRAP_CODE"];
    var correlationId = (Guid)context.Items["CorrelationId"]!;
    if (string.IsNullOrWhiteSpace(configured))
    {
        await audit.WriteAsync(correlationId, "auth.bootstrap.enroll", "DENY", "bootstrap_not_configured", cancellationToken);
        return Results.Json(new { error = "enrollment_denied", correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status401Unauthorized);
    }
    try
    {
        await identity.EnsureBootstrapAsync(configured, TimeSpan.FromMinutes(15), cancellationToken);
        var enrolled = await identity.EnrollOwnerAsync(request.BootstrapCode ?? string.Empty, request.InstallationId, request.PublicKey, request.Challenge ?? string.Empty, request.Signature ?? string.Empty, GetSessionLifetime(context), cancellationToken);
        await audit.WriteAsync(correlationId, "auth.bootstrap.enroll", "ALLOW", "owner_enrolled", cancellationToken);
        return Results.Ok(new { userId = enrolled.User.Id, deviceId = enrolled.Device.Id, sessionId = enrolled.SessionId, sessionToken = enrolled.SessionToken });
    }
    catch (InvalidOperationException)
    {
        await audit.WriteAsync(correlationId, "auth.bootstrap.enroll", "DENY", "owner_already_enrolled", cancellationToken);
        return Results.Conflict(new { error = "owner_already_enrolled", correlationId = context.Items["CorrelationId"] });
    }
    catch (UnauthorizedAccessException)
    {
        await audit.WriteAsync(correlationId, "auth.bootstrap.enroll", "DENY", "enrollment_denied", cancellationToken);
        return Results.Json(new { error = "enrollment_denied", correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    catch (Exception)
    {
        await audit.WriteAsync(correlationId, "auth.bootstrap.enroll", "DENY", "enrollment_unavailable", cancellationToken);
        return Results.Json(new { error = "enrollment_unavailable", correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/auth/local-token", async (HttpContext context, IdentityService identity, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    // Dev convenience only: explicit opt-in flag AND loopback caller. The page served
    // by Core itself uses this so the owner never hand-copies tokens. fail closed:
    // missing flag or non-loopback looks exactly like "no such endpoint".
    if (!configuration.GetValue<bool>("TOLIK_DEV_AUTO_TOKEN", false)) return Results.NotFound();
    var remote = context.Connection.RemoteIpAddress;
    Console.WriteLine($"[LOCALTOKEN] hit, remote={remote}");
    if (remote is not null && !IsLocalNetwork(remote)) return Results.NotFound();
    var issued = await identity.IssueLocalDevTokenAsync(GetSessionLifetime(context), cancellationToken);
    if (issued is null) return Results.Json(new { error = "no_owner_enrolled", correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status404NotFound);
    return Results.Ok(new { sessionToken = issued.Value.SessionToken, sessionId = issued.Value.SessionId, expiresAt = issued.Value.ExpiresAt });
});

app.MapPost("/auth/session/{sessionId:guid}/revoke", async (Guid sessionId, IdentityService identity, AuditWriter audit, HttpContext context, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : string.Empty;
    var principal = await identity.AuthenticateAsync(token, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (!await identity.OwnsSessionAsync(principal, sessionId, cancellationToken))
    {
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "auth.session.revoke", "DENY", "not_session_owner", principal, null, null, sessionId.ToString("D"), "denied", cancellationToken);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    var revoked = await identity.RevokeSessionAsync(sessionId, cancellationToken);
    await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "auth.session.revoke", revoked ? "ALLOW" : "DENY", revoked ? "session_revoked" : "session_not_found", principal, null, null, sessionId.ToString("D"), revoked ? "revoked" : "missing", cancellationToken);
    return revoked
        ? Results.Ok(new { revoked = true })
        : Results.NotFound(new { error = "session_not_found" });
});

app.MapPost("/auth/devices/{deviceId:guid}/revoke", async (Guid deviceId, IdentityService identity, AuditWriter audit, HttpContext context, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : string.Empty;
    var principal = await identity.AuthenticateAsync(token, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (!await identity.OwnsDeviceAsync(principal, deviceId, cancellationToken))
    {
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "auth.device.revoke", "DENY", "not_device_owner", principal, null, null, deviceId.ToString("D"), "denied", cancellationToken);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    var revoked = await identity.RevokeDeviceAsync(deviceId, cancellationToken);
    await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "auth.device.revoke", revoked ? "ALLOW" : "DENY", revoked ? "device_revoked" : "device_not_found", principal, null, null, deviceId.ToString("D"), revoked ? "revoked" : "missing", cancellationToken);
    return revoked
        ? Results.Ok(new { revoked = true })
        : Results.NotFound(new { error = "device_not_found" });
});

app.MapGet("/status", async (HttpContext context, IdentityService identity, TOLIK.Infrastructure.Authorization.CoreSecurityState securityState, CancellationToken cancellationToken) =>
{
    var token = context.Request.Headers.Authorization.ToString();
    var principal = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? await identity.AuthenticateAsync(token[7..], cancellationToken)
        : null;
    if (principal is null) return AuthenticationRequired(context);
    return Results.Ok(new { status = securityState.IsLocked ? "LOCKED" : "Healthy", locked = securityState.IsLocked, role = principal.Role, userId = principal.UserId, deviceId = principal.DeviceId });
});

// LOCKED state control (docs/SECURITY.md §27): manual lock (owner, audited) and
// out-of-band unlock (owner's unlock code — never automatic, no role unlocks through
// the regular path). The unlock code comes from TOLIK_SECURITY_UNLOCK_CODE runtime
// configuration; unset code → unlock impossible (restart with the code instead).
app.MapPost("/security/lock", async (LockRequest request, HttpContext context, IdentityService identity, TOLIK.Infrastructure.Authorization.CoreSecurityState securityState, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (!TolikRoles.IsOwner(principal.Role))
    {
        await audit.WriteAsync((Guid)context.Items["CorrelationId"]!, "security.lock", "DENY", "owner_role_required", cancellationToken);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest(new { error = "reason_required" });
    var transitioned = securityState.EnterLocked(request.Reason);
    await audit.WriteAsync((Guid)context.Items["CorrelationId"]!, "security.lock", transitioned ? "ALLOW" : "NOOP", transitioned ? "core_locked" : "already_locked", cancellationToken);
    return Results.Ok(new { locked = true, transitioned });
});

app.MapPost("/security/unlock", async (UnlockRequest request, HttpContext context, TOLIK.Infrastructure.Authorization.CoreSecurityState securityState, IdentityService identity, AuditWriter audit, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var configured = configuration["TOLIK_SECURITY_UNLOCK_CODE"];
    var correlationId = (Guid)context.Items["CorrelationId"]!;
    // Fail closed: unset code → unlock impossible (owner restarts with the code).
    if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(request.UnlockCode) ||
        !string.Equals(request.UnlockCode, configured, StringComparison.Ordinal))
    {
        await audit.WriteAsync(correlationId, "security.unlock", "DENY", "unlock_code_required", cancellationToken);
        return Results.Json(new { error = "unlock_denied", correlationId }, statusCode: StatusCodes.Status403Forbidden);
    }
    if (!securityState.IsLocked)
    {
        await audit.WriteAsync(correlationId, "security.unlock", "NOOP", "not_locked", cancellationToken);
        return Results.Ok(new { locked = false, transitioned = false });
    }
    var transitioned = securityState.TryUnlock($"owner_unlock:{(Guid)context.Items["CorrelationId"]!}");
    await audit.WriteAsync(correlationId, "security.unlock", transitioned ? "ALLOW" : "NOOP", transitioned ? "core_unlocked" : "not_locked", cancellationToken);
    return Results.Ok(new { locked = false, transitioned });
});

app.MapPost("/agents/connect", async (AgentRegistration registration, HttpContext context, IdentityService identity, AgentConnectionRegistry registry, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? await identity.AuthenticateAsync(header[7..], cancellationToken)
        : null;
    if (principal is null) return AuthenticationRequired(context);
    if (registration is null || !registration.Validate().IsValid)
    {
        await audit.WriteAsync((Guid)context.Items["CorrelationId"]!, "agent.connect", "DENY", "invalid_registration", cancellationToken);
        return Results.BadRequest(new { error = "invalid_agent_registration", correlationId = context.Items["CorrelationId"] });
    }
    try
    {
        if (!registry.Connect(registration, principal))
        {
            await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "agent.connect", "DENY", "agent_id_owned_by_another_user", principal, null, null, registration.AgentId.ToString("D"), "rejected", cancellationToken);
            return Results.Conflict(new { error = "agent_id_already_registered" });
        }
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "agent.connect", "ALLOW", "agent_registered", principal, null, null, registration.AgentId.ToString("D"), "connected", cancellationToken);
        Console.WriteLine($"[CORE REG] Agent {registration.AgentId} connected successfully");
        return Results.Ok(new { connected = true, agentId = registration.AgentId, capabilities = registration.AdvertisedCapabilities });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CORE REG] Exception: {ex.Message}");
        return Results.Json(new { error = "agent_connect_failed", detail = ex.Message, correlationId = context.Items["CorrelationId"] }, statusCode: StatusCodes.Status500InternalServerError);
    }
});
app.MapPost("/agents/heartbeat", async (AgentHeartbeatRequest request, HttpContext context, IdentityService identity, AgentConnectionRegistry registry, AuditWriter audit, CancellationToken cancellationToken) =>
{
    if (request is null) return Results.BadRequest(new { error = "invalid_heartbeat" });
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var touched = registry.Touch(request.AgentId, principal.UserId);
    // Liveness is not an authorization decision: allow-auditing every 2s heartbeat
    // floods the hash-chained audit table (~43k rows/day). Denials stay audited —
    // a heartbeat from an unregistered/foreign agent is a security signal (§16, T5/T10).
    if (!touched)
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "agent.heartbeat", "DENY", "agent_not_registered", principal, null, null, request.AgentId.ToString("D"), "missing", cancellationToken);
    return touched
        ? Results.Ok(new { agentId = request.AgentId, status = "Connected", observedAt = DateTimeOffset.UtcNow })
        : Results.NotFound(new { error = "agent_not_registered" });
});
app.MapPost("/agents/{agentId:guid}/disconnect", async (Guid agentId, HttpContext context, IdentityService identity, AgentConnectionRegistry registry, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var disconnected = registry.Disconnect(agentId, principal.UserId);
    await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "agent.disconnect", disconnected ? "ALLOW" : "DENY", disconnected ? "agent_disconnected" : "agent_not_owned", principal, null, null, agentId.ToString("D"), disconnected ? "disconnected" : "rejected", cancellationToken);
    return disconnected
        ? Results.Ok(new { disconnected = true, agentId })
        : Results.NotFound(new { error = "agent_not_registered" });
});
app.MapGet("/agents/{agentId:guid}/commands/poll", async (Guid agentId, HttpContext context, IdentityService identity, AgentConnectionRegistry registry, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var poll = await registry.PollAsync(agentId, principal.UserId, cancellationToken);
    if (poll.Command is not null) Console.WriteLine($"[CORE POLL] Agent {agentId}, delivering command: {poll.Command.CommandId}");
    if (!poll.Owned)
    {
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "agent.command.poll", "DENY", "agent_not_owned", principal, null, null, agentId.ToString("D"), "rejected", cancellationToken);
        return Results.NotFound();
    }
    var command = poll.Command;
    if (command is not null) await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "agent.command.poll", "ALLOW", "command_delivered", principal, command.Capability, ToolRiskLevel.LowRisk, agentId.ToString("D"), command.CommandId.ToString("D"), cancellationToken);
    return command is null ? Results.NoContent() : Results.Ok(command);
});
app.MapPost("/agents/{agentId:guid}/commands/complete", async (Guid agentId, AgentCommandResponse response, HttpContext context, IdentityService identity, AgentConnectionRegistry registry, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (response is null) return Results.BadRequest(new { error = "invalid_command_response" });
    var accepted = registry.Complete(agentId, principal.UserId, response);
    if (!registry.Owns(agentId, principal.UserId) && !accepted)
    {
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "agent.command.complete", "DENY", "agent_not_owned", principal, null, null, agentId.ToString("D"), "rejected", cancellationToken);
        return Results.NotFound();
    }
    await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "agent.command.complete", accepted ? "ALLOW" : "DENY", accepted ? "command_completed" : "command_not_found", principal, null, null, agentId.ToString("D"), response.Outcome.ToString(), cancellationToken);
    return accepted ? Results.Ok(new { accepted = true }) : Results.NotFound(new { error = "command_not_found" });
});
app.MapPost("/agents/tools/read-file", async (FileToolRequest request, HttpContext context, IdentityService identity, FileToolService files, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    try { return Results.Ok(new { content = await files.ReadAsync(request.Path, cancellationToken) }); }
    catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
    catch (FileNotFoundException) { return Results.NotFound(); }
});

app.MapPost("/tasks", async (TaskRequest request, HttpContext context, IdentityService identity, TOLIK.Infrastructure.Authorization.CoreSecurityState securityState, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var correlation = (Guid)context.Items["CorrelationId"]!;
    var header = context.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : string.Empty;
    var principal = await identity.AuthenticateAsync(token, cancellationToken);
    if (principal is null)
    {
        await audit.WriteAsync(correlation, "task.request", "DENY", "authentication_required", cancellationToken);
        return AuthenticationRequired(context);
    }

    var decision = AuthorizationPolicy.Evaluate(new AuthorizationRequest(
        principal, AgentCapability.RunBuild, ToolRiskLevel.LowRisk, "task", "task", false, CoreLocked: securityState.IsLocked));
    if (decision is AuthorizationDecision.Deny)
    {
        await audit.WriteAuthorizationAsync(correlation, "task.request", "DENY", "authorization_denied", principal,
            AgentCapability.RunBuild, ToolRiskLevel.LowRisk, "task", "denied", cancellationToken);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var task = context.RequestServices.GetRequiredService<TaskLifecycle>().Create(request.Description ?? string.Empty);
    realtime.StampTaskOwner(task.Id, principal.UserId);
    await using (var db = await context.RequestServices.GetRequiredService<IDbContextFactory<TolikDbContext>>().CreateDbContextAsync(cancellationToken))
    {
        db.Tasks.Add(new TolikTaskEntity { Id = task.Id, UserId = principal.UserId, Description = task.Description, Status = task.Status.ToString(), CreatedAt = task.CreatedAt });
        await db.SaveChangesAsync(cancellationToken);
    }
    await audit.WriteAuthorizationAsync(correlation, "task.request", "ALLOW", "task_created", principal,
        AgentCapability.RunBuild, ToolRiskLevel.LowRisk, "task", "execution_unavailable", cancellationToken);
    return Results.Accepted($"/tasks/{task.Id}", task);
});
app.MapGet("/tasks/{taskId:guid}", async (Guid taskId, HttpContext context, IdentityService identity, TaskLifecycle lifecycle, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    var persisted = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.UserId == principal.UserId, cancellationToken);
    return persisted is null ? Results.NotFound() : Results.Ok(persisted);
});
app.MapPost("/tasks/{taskId:guid}/cancel", async (Guid taskId, HttpContext context, IdentityService identity, TaskLifecycle lifecycle, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    var persisted = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.UserId == principal.UserId, cancellationToken);
    if (persisted is null) return Results.NotFound();
    if (!lifecycle.TryCancel(taskId)) return Results.Conflict(new { error = "invalid_task_state" });
    persisted.Status = "Cancelled"; persisted.CancelledAt = DateTimeOffset.UtcNow; persisted.CompletedAt = persisted.CancelledAt; await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(lifecycle.Get(taskId));
});
app.MapGet("/tasks/{taskId:guid}/events", async (Guid taskId, HttpContext context, IdentityService identity, TaskLifecycle lifecycle, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    if (!await db.Tasks.AnyAsync(x => x.Id == taskId && x.UserId == principal.UserId, cancellationToken)) return Results.NotFound();
    return Results.Ok(lifecycle.GetEvents(taskId));
});
app.MapPost("/tasks/{taskId:guid}/plan", async (Guid taskId, PlanRequest request, HttpContext context, IdentityService identity, TaskLifecycle lifecycle, BoundedPlanner planner, LlmTaskPlanner autoPlanner, ModelRouter router, ModelCompletionClient completion, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    if (!await db.Tasks.AnyAsync(x => x.Id == taskId && x.UserId == principal.UserId, cancellationToken)) return Results.NotFound();
    var task = lifecycle.Get(taskId);
    if (task is null) return Results.NotFound();
    try { return Results.Ok(planner.Create(request.Request ?? task.Description, request.MaxSteps, TimeSpan.FromSeconds(request.BudgetSeconds), await ResolveStepsAsync(request, task.Description, autoPlanner, router, completion, cancellationToken))); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_plan_bounds" }); }
});
app.MapGet("/tasks/{taskId:guid}/confirmations", async (Guid taskId, HttpContext context, IdentityService identity, [FromServices] TOLIK.Infrastructure.Authorization.ConfirmationStore confirmations, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    if (!await db.Tasks.AnyAsync(x => x.Id == taskId && x.UserId == principal.UserId, cancellationToken)) return Results.NotFound();
    return Results.Ok(confirmations.ListForTask(taskId).Select(x => new
    {
        x.Id, x.StepNumber, capability = x.Capability.ToString(), x.Resource, x.Approved,
        effectiveRisk = x.EffectiveRisk.ToString(), x.RequiredMethod, x.ExpiresAt
    }));
});
app.MapPost("/tasks/{taskId:guid}/confirmations/{confirmationId:guid}/approve", async (Guid taskId, Guid confirmationId, HttpContext context, IdentityService identity, [FromServices] TOLIK.Infrastructure.Authorization.ConfirmationStore confirmations, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var confirmation = confirmations.Get(confirmationId);
    if (confirmation is null || confirmation.TaskId != taskId)
        return Results.NotFound(new { error = "confirmation_not_found" });
    if (!TolikRoles.IsOwner(principal.Role))
    {
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "confirmation.approve", "DENY", "owner_role_required", principal,
            confirmation.Capability, confirmation.EffectiveRisk, confirmation.Resource, "denied", cancellationToken);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    // В§9: critical escalations require strong auth (MFA) вЂ” plain approval is not enough.
    if (confirmation.RequiresMfa)
    {
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "confirmation.approve", "DENY", "strong_auth_required", principal,
            confirmation.Capability, confirmation.EffectiveRisk, confirmation.Resource, "denied", cancellationToken);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    if (!confirmations.Approve(confirmationId))
        return Results.Conflict(new { error = "confirmation_invalid_state" });
    await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "confirmation.approve", "ALLOW", "owner_approved", principal,
        confirmation.Capability, confirmation.EffectiveRisk, confirmation.Resource, "approved", cancellationToken);
    return Results.Ok(new { approved = true, confirmationId, note = "Re-run the execute request; the bound confirmation will be consumed once." });
});
app.MapPost("/tasks/{taskId:guid}/execute", async (Guid taskId, PlanRequest request, HttpContext context, IdentityService identity, TaskLifecycle lifecycle, BoundedPlanner planner, LlmTaskPlanner autoPlanner, ModelRouter router, ModelCompletionClient completion, PlanExecutionGuard guard, AgentCommandDispatcher dispatcher, TOLIK.Infrastructure.Authorization.AuthorizationLeaseStore leases, TOLIK.Infrastructure.Authorization.CoreSecurityState securityState, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    if (!await db.Tasks.AnyAsync(x => x.Id == taskId && x.UserId == principal.UserId, cancellationToken)) return Results.NotFound();
    var task = lifecycle.Get(taskId);
    if (task is null) return Results.NotFound();
    if (!lifecycle.TryStart(taskId, out var taskCancellation)) return Results.Conflict(new { error = "invalid_task_state" });
    // Lease (stage 2c of the interface/ port): a short-lived (60s default) per-run
    // authorization bound to the authenticated session; its ceiling comes from the
    // principal's role (unknown/unparsed role → read-only, fail closed). Every step
    // validates it and its expiry feeds the authorization policy.
    var parsedRunRole = TolikRoles.Parse(principal.Role);
    var leaseCeiling = parsedRunRole is { } runRole ? (TolikRoles.CeilingFor(runRole) ?? ToolRiskLevel.ReadOnly) : ToolRiskLevel.ReadOnly;
    var lease = leases.Issue(principal.SessionId, leaseCeiling);
    using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, taskCancellation);
    var completed = new List<string>();
    try
    {
        var persistedTask = await db.Tasks.SingleAsync(x => x.Id == taskId && x.UserId == principal.UserId, executionCancellation.Token);
        persistedTask.Status = "Running";
        await db.SaveChangesAsync(executionCancellation.Token);
        var plan = planner.Create(request.Request ?? task.Description, request.MaxSteps, TimeSpan.FromSeconds(request.BudgetSeconds), await ResolveStepsAsync(request, task.Description, autoPlanner, router, completion, executionCancellation.Token));
        var confirmations = context.RequestServices.GetRequiredService<TOLIK.Infrastructure.Authorization.ConfirmationStore>();
        var stepNumber = 0;
        var results = await guard.ExecuteAsync(plan, async (step, token) =>
        {
            var currentStep = Interlocked.Increment(ref stepNumber);
            lifecycle.ReportProgress(taskId, $"step={currentStep}/{plan.Steps.Count};action={step.Action}");

            // Guarded dispatch: every step is authorized with its effective risk; escalations
            // demand a bound confirmation (stage 2 of the interface/ port, В§11/T13).
            async Task<string> DispatchGuardedAsync(AgentCapability capability, string resource)
            {
                var declaredRisk = TOLIK.Infrastructure.Authorization.ConfirmationStore.DeclaredRiskFor(capability);
                var (escalatedRisk, _) = TOLIK.Infrastructure.Authorization.ToolEscalation.Evaluate(capability, resource);
                var effectiveRisk = escalatedRisk > declaredRisk ? escalatedRisk : declaredRisk;
                // Lease validation per step (stage 2c): unknown, revoked or expired
                // leases authorize nothing (fail closed).
                var validLease = leases.Validate(lease.Id);
                if (validLease is null)
                    throw new UnauthorizedAccessException("lease_invalid:expired_or_revoked");
                var confirmationValid = confirmations.Consume(taskId, currentStep, capability, resource);
                var decision = AuthorizationPolicy.Evaluate(new AuthorizationRequest(
                    principal, capability, declaredRisk, resource, resource, confirmationValid, MfaValid: false,
                    OfflineMode: router.IsOfflineMode, CoreLocked: securityState.IsLocked, AuthorizationExpiresAt: validLease.ExpiresAt));
                if (decision is AuthorizationDecision.ConfirmationRequired)
                {
                    var pending = confirmations.Issue(taskId, currentStep, capability, resource, effectiveRisk);
                    throw new TOLIK.Infrastructure.Authorization.ConfirmationRequiredException(pending);
                }
                if (decision is AuthorizationDecision.Deny)
                    throw new UnauthorizedAccessException($"capability_denied:{capability};risk={effectiveRisk}");
                // Agent execution failure is a step outcome, not a transport error:
                // record partial progress and stop without cascading (§22 autonomy
                // stays fail-closed: no further steps run after a failure).
                try
                {
                    var outcome = await dispatcher.ExecuteAsync(principal.UserId, capability, resource, token);
                    completed.Add(outcome);
                    return outcome;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or TaskCanceledException)
                {
                    if (executionCancellation.IsCancellationRequested) throw;
                    throw new StepFailedException($"step {currentStep}: {ex.Message}", ex);
                }
            }

            const string prefix = AgentStepSyntax.ReadFile;
            if (step.Action.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = step.Action[prefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("read_file requires a path.");
                return await DispatchGuardedAsync(AgentCapability.ReadFile, path);
            }
            const string listPrefix = AgentStepSyntax.ListDirectory;
            if (step.Action.StartsWith(listPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = step.Action[listPrefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("list_directory requires a path.");
                return await DispatchGuardedAsync(AgentCapability.ListDirectory, path);
            }
            const string processPrefix = AgentStepSyntax.GetProcessStatus;
            if (step.Action.StartsWith(processPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = step.Action[processPrefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsWhiteSpace)) throw new ArgumentException("get_process_status requires a process name.");
                return await DispatchGuardedAsync(AgentCapability.GetProcessStatus, name);
            }
            const string runProcessPrefix = AgentStepSyntax.RunProcess;
            if (step.Action.StartsWith(runProcessPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // ARCHITECTURE (AGENTS.md §7): Core never executes OS processes itself.
                // The command is dispatched to the connected Agent that advertises RunProcess.
                var processSpec = step.Action[runProcessPrefix.Length..].Trim();
                if (processSpec.Length == 0 || processSpec.Length > 512) throw new ArgumentException("run_process requires a spec: executable;args...");
                return await DispatchGuardedAsync(AgentCapability.RunProcess, processSpec);
            }
            // Self-improvement steps (§21/§53): coding-agent assistance and mandatory
            // build+test gates, all through the same authorization/confirmation guard.
            const string runCodexPrefix = AgentStepSyntax.RunCodex;
            if (step.Action.StartsWith(runCodexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var spec = step.Action[runCodexPrefix.Length..].Trim();
                if (spec.Length == 0 || spec.Length > 4608) throw new ArgumentException("run_codex requires a spec: workingDir|prompt...");
                return await DispatchGuardedAsync(AgentCapability.RunCodex, spec);
            }
            const string runOpenCodePrefix = AgentStepSyntax.RunOpenCode;
            if (step.Action.StartsWith(runOpenCodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var spec = step.Action[runOpenCodePrefix.Length..].Trim();
                if (spec.Length == 0 || spec.Length > 4608) throw new ArgumentException("run_opencode requires a spec: workingDir|prompt...");
                return await DispatchGuardedAsync(AgentCapability.RunOpenCode, spec);
            }
            const string runBuildPrefix = AgentStepSyntax.RunBuild;
            if (step.Action.StartsWith(runBuildPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = step.Action[runBuildPrefix.Length..].Trim();
                if (path.Length == 0 || path.Length > 512) throw new ArgumentException("run_build requires a project path.");
                return await DispatchGuardedAsync(AgentCapability.RunBuild, path);
            }
            const string runTestsPrefix = AgentStepSyntax.RunTests;
            if (step.Action.StartsWith(runTestsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = step.Action[runTestsPrefix.Length..].Trim();
                if (path.Length == 0 || path.Length > 512) throw new ArgumentException("run_tests requires a project path.");
                return await DispatchGuardedAsync(AgentCapability.RunTests, path);
            }
            throw new UnauthorizedAccessException("Action is not available through the bounded Agent dispatcher.");
        }, executionCancellation.Token);
        if (!lifecycle.Complete(taskId, $"steps={results.Count}"))
        {
            persistedTask.Status = "Cancelled";
            persistedTask.CancelledAt = DateTimeOffset.UtcNow;
            persistedTask.CompletedAt = persistedTask.CancelledAt;
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        persistedTask.Status = "Completed";
        persistedTask.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        return Results.Ok(new { taskId, results });
    }
    catch (OperationCanceledException) when (taskCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
    {
        lifecycle.TryCancel(taskId);
        var cancelled = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.UserId == principal.UserId, CancellationToken.None);
        if (cancelled is not null && cancelled.Status is "Pending" or "Running")
        {
            cancelled.Status = "Cancelled";
            cancelled.CancelledAt = DateTimeOffset.UtcNow;
            cancelled.CompletedAt = cancelled.CancelledAt;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (TOLIK.Infrastructure.Authorization.ConfirmationRequiredException ex)
    {
        // Stage 2: the step is parked, not failed вЂ” approve and re-execute to continue.
        lifecycle.Pause(taskId, $"awaiting_confirmation:{ex.Confirmation.Id}");
        var auditWriter = context.RequestServices.GetRequiredService<AuditWriter>();
        await auditWriter.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "task.confirmation", "DENY", "confirmation_required", principal,
            ex.Confirmation.Capability, ex.Confirmation.EffectiveRisk, ex.Confirmation.Resource, "awaiting_confirmation", CancellationToken.None);
        return Results.Accepted($"/tasks/{taskId}/confirmations/{ex.Confirmation.Id}", new
        {
            taskId,
            needsConfirmation = true,
            confirmationId = ex.Confirmation.Id,
            step = ex.Confirmation.StepNumber,
            capability = ex.Confirmation.Capability.ToString(),
            resource = ex.Confirmation.Resource,
            effectiveRisk = ex.Confirmation.EffectiveRisk.ToString(),
            requiredMethod = ex.Confirmation.RequiredMethod,
            expiresAt = ex.Confirmation.ExpiresAt
        });
    }
    catch (UnauthorizedAccessException ex)
    {
        lifecycle.Fail(taskId, ex.Message);
        var failed = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.UserId == principal.UserId, CancellationToken.None);
        if (failed is not null) { failed.Status = "Failed"; failed.CompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(CancellationToken.None); }
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    catch (ArgumentException)
    {
        lifecycle.Fail(taskId, "invalid_plan_bounds");
        var failed = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.UserId == principal.UserId, CancellationToken.None);
        if (failed is not null) { failed.Status = "Failed"; failed.CompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(CancellationToken.None); }
        return Results.BadRequest(new { error = "invalid_plan_bounds" });
    }
    catch (StepFailedException ex)
    {
        lifecycle.Fail(taskId, ex.Message);
        var failed = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.UserId == principal.UserId, CancellationToken.None);
        if (failed is not null) { failed.Status = "Failed"; failed.CompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(CancellationToken.None); }
        return Results.Ok(new { taskId, results = completed, failed = true, error = ex.Message });
    }
    catch (Exception ex)
    {
        lifecycle.Fail(taskId, ex.Message);
        var failed = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.UserId == principal.UserId, CancellationToken.None);
        if (failed is not null) { failed.Status = "Failed"; failed.CompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(CancellationToken.None); }
        throw;
    }
});
app.MapPost("/memory", async (MemoryRequest request, HttpContext context, IdentityService identity, ScopedMemory memory, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    try { var item = memory.Add(principal.UserId, request.Scope, request.Content ?? string.Empty, request.Source ?? "user", request.Confidence, request.Origin ?? MemoryOrigin.Lived); await using var db = await factory.CreateDbContextAsync(cancellationToken); db.Memories.Add(new TolikMemoryEntity { Id = item.Id, OwnerId = item.OwnerId, Scope = item.Scope.ToString(), Content = item.Content, Source = item.Source, Confidence = item.Confidence, CreatedAt = item.CreatedAt, Origin = item.Origin }); await db.SaveChangesAsync(cancellationToken); return Results.Ok(item); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_memory" }); }
});
app.MapGet("/memory/search", async (string q, HttpContext context, IdentityService identity, ScopedMemory memory, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    var local = memory.Search(principal.UserId, q ?? string.Empty);
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    var persisted = await db.Memories.Where(x => (x.Scope != "Private" || x.OwnerId == principal.UserId) && x.Content.Contains(q ?? string.Empty)).OrderByDescending(x => x.Confidence).ToListAsync(cancellationToken);
    return Results.Ok(new { local, persisted });
});
app.MapDelete("/memory/{memoryId:guid}", async (Guid memoryId, HttpContext context, IdentityService identity, ScopedMemory memory, DurableMemoryStore durableMemory, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var deleted = await durableMemory.DeleteAsync(principal.UserId, memoryId, cancellationToken);
    if (deleted) memory.Remove(principal.UserId, memoryId);
    await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "memory.delete", deleted ? "ALLOW" : "DENY", deleted ? "owner_deleted" : "not_found_or_not_owned", principal, null, null, memoryId.ToString("D"), deleted ? "deleted" : "missing", cancellationToken);
    return deleted ? Results.Ok(new { deleted = true, memoryId }) : Results.NotFound(new { error = "memory_not_found" });
});
app.MapGet("/audit", async (int? take, int? skip, HttpContext context, IdentityService identity, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    // Audit transparency (§38/§15): the owner can inspect decisions. Privacy
    // boundary (§18): non-owners see only their own user-scoped events.
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var count = take ?? 50;
    var offset = skip ?? 0;
    if (count is < 1 or > 100 || offset < 0) return Results.BadRequest(new { error = "invalid_pagination" });
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    var query = db.AuditEvents.AsNoTracking().OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id);
    if (TolikRoles.IsOwner(principal.Role))
        return Results.Ok(await query.Skip(offset).Take(count).ToListAsync(cancellationToken));
    var ownId = principal.UserId.ToString("D");
    return Results.Ok(await query.Where(x => x.UserId == ownId).Skip(offset).Take(count).ToListAsync(cancellationToken));
});
app.MapPost("/people", async (PersonRequest request, HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    try
    {
        var person = await personal.AddPersonAsync(principal.UserId, request.Name ?? string.Empty, request.Relationship ?? string.Empty, request.Scope, cancellationToken);
        return Results.Ok(person);
    }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_person" }); }
});
app.MapGet("/people", async (HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    return Results.Ok(await personal.GetPeopleAsync(principal.UserId, cancellationToken));
});
app.MapPost("/humor", async (HumorRequest request, HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    try
    {
        var item = await personal.AddHumorAsync(principal.UserId, request.Scope, request.Content ?? string.Empty, request.Source ?? "user", cancellationToken);
        return Results.Ok(item);
    }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_humor" }); }
});
app.MapGet("/humor", async (HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    return Results.Ok(await personal.GetHumorAsync(principal.UserId, cancellationToken));
});
app.MapPost("/personality", async (PersonalityRequest request, HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    try { return Results.Ok(await personal.SetPersonalityAsync(principal.UserId, request.Tone ?? string.Empty, request.Style ?? string.Empty, request.UseHumor, cancellationToken)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_personality" }); }
});
app.MapGet("/personality", async (HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    return Results.Ok(await personal.GetPersonalityAsync(principal.UserId, cancellationToken));
});
app.MapPost("/family-lore", async (FamilyLoreRequest request, HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    try { return Results.Ok(await personal.AddFamilyLoreAsync(principal.UserId, request.Content ?? string.Empty, cancellationToken)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_family_lore" }); }
});
app.MapGet("/family-lore", async (HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    return Results.Ok(await personal.GetFamilyLoreAsync(principal.UserId, cancellationToken));
});
app.MapPost("/relationships", async (RelationshipRequest request, HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    try { return Results.Ok(await personal.AddRelationshipAsync(principal.UserId, request.Scope, request.PersonId, request.Relation ?? string.Empty, cancellationToken)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_relationship" }); }
});
app.MapGet("/relationships", async (HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    return Results.Ok(await personal.GetRelationshipsAsync(principal.UserId, cancellationToken));
});
app.MapPost("/events", async (PersonalEventRequest request, HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    try { return Results.Ok(await personal.AddEventAsync(principal.UserId, request.Scope, request.Type ?? string.Empty, request.Content ?? string.Empty, request.OccurredAt ?? DateTimeOffset.UtcNow, cancellationToken)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_event" }); }
});
app.MapGet("/events", async (HttpContext context, IdentityService identity, PersonalContextStore personal, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    return Results.Ok(await personal.GetEventsAsync(principal.UserId, cancellationToken));
});
app.MapPost("/tasks/{taskId:guid}/feedback", async (Guid taskId, TaskFeedbackRequest request, HttpContext context, IdentityService identity, AuditWriter audit, LearnedRuleStore rules, IDbContextFactory<TolikDbContext> factory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (request is null) return Results.BadRequest(new { error = "invalid_feedback" });

    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId && x.UserId == principal.UserId, cancellationToken);
    if (task is null) return Results.NotFound(new { error = "task_not_found" });

    await audit.WriteAuthorizationAsync(
        (Guid)context.Items["CorrelationId"]!,
        "task.feedback",
        "ALLOW",
        request.Positive ? "positive_feedback" : "negative_feedback",
        principal, null, null, taskId.ToString("D"), request.Positive ? "positive" : "negative", cancellationToken);

    // A comment becomes a candidate learned rule with moderate confidence (В§21: never
    // create dangerous permanent behavior from one ambiguous interaction).
    // Repeated identical feedback strengthens the existing rule instead of duplicating it.
    if (!string.IsNullOrWhiteSpace(request.Comment) && request.Comment.Length <= 2048)
    {
        var prefix = request.Positive ? "РџРѕР»cЊР·РѕРІР°c‚РµР»cЊ РѕРґРѕР±cЂРёР»: " : "РџРѕР»cЊР·РѕРІР°c‚РµР»cЊ РїРѕРїcЂР°РІРёР»: ";
        var ruleText = prefix + request.Comment.Trim();
        var existing = await rules.FindByTextAsync(principal.UserId, ruleText, cancellationToken);
        if (existing is null)
            await rules.AddAsync(principal.UserId, ruleText, 0.6, "user_feedback", cancellationToken);
        else
            await rules.BumpConfidenceAsync(existing.Id, principal.UserId, 0.1, cancellationToken);
    }

    return Results.Ok(new { accepted = true, taskId, positive = request.Positive });
});
app.MapGet("/rules", async (HttpContext context, IdentityService identity, LearnedRuleStore rules, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var top = await rules.GetTopAsync(principal.UserId, 32, cancellationToken);
    return Results.Ok(top);
});
app.MapPost("/audio/play", async (PlaySoundRequest request, HttpContext context, IdentityService identity, AgentCommandDispatcher dispatcher, AuditWriter audit, DurableMemoryStore durableMemory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (request is null || string.IsNullOrWhiteSpace(request.Category) || string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "invalid_sound_request" });

    // В§17: rate limited вЂ” no more than one reaction sound per 10 seconds per user.
    if (!soundboardLimiter.TryAcquire(principal.UserId))
    {
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "sound.play", "DENY", "rate_limited", principal, AgentCapability.PlaySound, ToolRiskLevel.LowRisk, $"{request.Category}/{request.Name}", "denied", cancellationToken);
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    try
    {
        var result = await dispatcher.ExecuteAsync(principal.UserId, AgentCapability.PlaySound, $"{request.Category};{request.Name}", cancellationToken);
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "sound.play", "ALLOW", "played", principal, AgentCapability.PlaySound, ToolRiskLevel.LowRisk, $"{request.Category}/{request.Name}", "played", cancellationToken);
        return Results.Ok(new { played = true, sound = $"{request.Category}/{request.Name}", detail = result });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Problem("No connected agent advertises PlaySound.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { error = "sound_not_available", detail = ex.Message });
    }
});
app.MapGet("/audio/library", (HttpContext context) =>
{
    // Lists the soundboard library shape (В§17); actual files are resolved by the Agent.
    return Results.Ok(new
    {
        categories = new[]
        {
            new { name = "reactions", description = "РљРѕcЂРѕc‚РєРёРµ cЂРµР°Рєc†РёРё РЅР° c„cЂР°Р·c‹" },
            new { name = "memes", description = "Р›cЋР±РёРјc‹Рµ РјРµРјc‹" },
            new { name = "family", description = "РЎРµРјРµР№РЅc‹Рµ Р·РІcѓРєРё" },
            new { name = "system", description = "РЎРёcc‚РµРјРЅc‹Рµ cРѕР±c‹c‚РёcЏ (build done, reminder)" }
        },
        note = "Files live in TOLIK_AUDIO_ROOT/<category>/ on the machine where TOLIK.Agent runs."
    });
});
app.MapGet("/status/voice", async (HttpContext context, IdentityService identity, VoiceRuntimeState runtimeState, ILocalVoiceRuntime tts, WhisperSttProvider stt, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var (mode, changedAt, reason) = runtimeState.Snapshot();
    var ttsHealth = await tts.CheckHealthAsync(cancellationToken);
    var sttHealth = await stt.CheckHealthAsync(cancellationToken);
    return Results.Ok(new { runtimeMode = mode.ToString(), changedAt, reason, tts = ttsHealth.Status, ttsAvailable = ttsHealth.Available, stt = sttHealth.Status, sttAvailable = sttHealth.Available });
});
// Remembered model-routing preference (ask-policy, docs/ROADMAP.md model policy):
// the owner answers the cloud-vs-local ask for coding; the choice is remembered
// per user and applied to future coding-route resolution. In-memory per process.
app.MapPost("/models/preference", async (ModelPreferenceRequest request, HttpContext context, IdentityService identity, TOLIK.Infrastructure.Models.ModelPreferenceStore preferences, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var mode = request.Mode?.Trim();
    TOLIK.Infrastructure.Models.ModelRoutingPreference? preference = mode?.ToLowerInvariant() switch
    {
        "cloud" => TOLIK.Infrastructure.Models.ModelRoutingPreference.Cloud,
        "local" => TOLIK.Infrastructure.Models.ModelRoutingPreference.Local,
        _ => null
    };
    if (preference is null) return Results.BadRequest(new { error = "invalid_mode" });
    preferences.Set(principal.UserId, preference.Value);
    return Results.Ok(new { preference = mode!.ToLowerInvariant(), remembered = true });
});
app.MapGet("/models/preference", async (HttpContext context, IdentityService identity, TOLIK.Infrastructure.Models.ModelPreferenceStore preferences, ModelRouter router, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var remembered = preferences.Get(principal.UserId);
    return Results.Ok(new { preference = remembered?.ToString()?.ToLowerInvariant(), offlineMode = router.IsOfflineMode });
});
app.MapDelete("/models/preference", async (HttpContext context, IdentityService identity, TOLIK.Infrastructure.Models.ModelPreferenceStore preferences, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    preferences.Clear(principal.UserId);
    return Results.Ok(new { preference = (string?)null, remembered = false });
});

app.MapPost("/chat", async (ChatRequest request, HttpContext context, IdentityService identity, ModelRouter router, ModelCompletionClient completion, ScopedMemory memory, DurableMemoryStore durableMemory, FileToolService files, LearnedRuleStore rules, PersonalContextStore personalStore, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "message_required" });
    var action = request.Message.Trim();
    // Deterministic intent routing (В§24: LLM never decides permissions/actions alone).
    var intent = IntentRouter.Classify(action);
    var dispatcher = new AgentCommandDispatcher(context.RequestServices.GetRequiredService<AgentConnectionRegistry>());
    switch (intent.Kind)
    {
        case ChatIntentKind.SwitchVoiceMode:
        {
            var runtimeState = context.RequestServices.GetRequiredService<VoiceRuntimeState>();
            if (!runtimeState.TrySwitch(intent.Argument, "chat_command"))
                return Results.BadRequest(new { error = "invalid_voice_mode" });
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK: переключен голосовой режим на {intent.Argument}", "chat.action.voice_mode", 1, cancellationToken);
            return Results.Ok(new { answer = $"Голосовой режим: {intent.Argument}", action = "voice_mode", details = intent.Argument, source = "core" });
        }
        case ChatIntentKind.Reminder:
        {
            var parts = intent.Argument.Split('|');
            if (parts.Length != 3) break;
            var amount = int.Parse(parts[0]);
            if (amount is < 1 or > 100000) return Results.BadRequest(new { error = "invalid_delay" });
            var unit = parts[1].ToLowerInvariant();
            var delay = unit.StartsWith('c') ? TimeSpan.FromSeconds(amount)
                : unit.StartsWith('c') ? TimeSpan.FromHours(amount)
                : TimeSpan.FromMinutes(amount);
            var reminderText = parts[2].Trim();
            var proactive = context.RequestServices.GetRequiredService<TOLIK.Infrastructure.Persistence.ProactiveStore>();
            var scheduled = await proactive.ScheduleAsync(principal.UserId, reminderText, "Normal", DateTimeOffset.UtcNow.Add(delay), "user_reminder", cancellationToken);
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK записал напоминание: {reminderText} (через {delay})", "proactive.reminder", 1, cancellationToken);
            return Results.Ok(new { answer = $"Хорошо, напомню через {delay.TotalMinutes:0.#} мин: {reminderText}", action = "reminder", details = reminderText, dueAt = scheduled.DueAt, source = "core" });
        }
        case ChatIntentKind.StoreMemory:
        {
            var fact = intent.Argument.Trim();
            if (fact.Length == 0 || fact.Length > 8192) return Results.BadRequest(new { error = "invalid_memory" });
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, fact, "user_explicit", 1, cancellationToken);
            return Results.Ok(new { answer = "Запомнил: " + fact, action = "store_memory", details = fact, source = "core" });
        }
        case ChatIntentKind.StoreFamilyMeme:
        {
            var meme = intent.Argument.Trim();
            if (meme.Length == 0 || meme.Length > 8192) return Results.BadRequest(new { error = "invalid_meme" });
            var personal = context.RequestServices.GetRequiredService<PersonalContextStore>();
            await personal.AddHumorAsync(principal.UserId, MemoryScope.Shared, meme, "family_meme", cancellationToken);
            await personal.AddFamilyLoreAsync(principal.UserId, meme, cancellationToken);
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Shared, $"Семейный мем: {meme}", "family.meme", 1, cancellationToken);
            return Results.Ok(new { answer = "Отлично, это теперь наш семейный мем.", action = "store_family_meme", details = meme, source = "core" });
        }
        case ChatIntentKind.StoreFunny:
        {
            var personal = context.RequestServices.GetRequiredService<PersonalContextStore>();
            await personal.AddHumorAsync(principal.UserId, MemoryScope.Private, action, "user_feedback", cancellationToken);
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"Было смешно: {action}", "humor.funny", 0.9, cancellationToken);
            return Results.Ok(new { answer = "Ха, запомнил, это смешно.", action = "store_humor", source = "core" });
        }
        case ChatIntentKind.RecallMemory:
        {
            var query = intent.Argument.Trim();
            var found = await durableMemory.SearchAsync(principal.UserId, query, 8, cancellationToken);
            if (found.Count == 0)
                return Results.Ok(new { answer = $"Не помню ничего про «{query}».", action = "recall_memory", source = "core" });
            var answer = string.Join("\n• ", found.Select(m => $"{m.Content} ({m.CreatedAt:dd.MM})"));
            answer = "Помню вот это:\n• " + answer;
            await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK: задача про «{query}»", "chat.assistant", 0.8, cancellationToken);
            return Results.Ok(new { answer, action = "recall_memory", count = found.Count, source = "core" });
        }
        case ChatIntentKind.OpenApp:
        {
            var appName = intent.Argument.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(appName) || appName.Length > 256) return Results.BadRequest(new { error = "invalid_app_name" });
            if (appName.Contains("..", StringComparison.Ordinal) || appName.Contains('\\') || appName.Contains('/'))
                return Results.BadRequest(new { error = "invalid_app_name" });
            appName = ResolveAppAlias(appName);
            try
            {
                var agentResult = await dispatcher.ExecuteAsync(principal.UserId, AgentCapability.RunProcess, appName, cancellationToken);
                await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK: Рѕc‚РєcЂc‹Р» '{appName}' ({agentResult})", "chat.action.run_process", 1, cancellationToken);
                return Results.Ok(new { answer = $"Открыто: {appName}", action = "run_process", details = appName, source = "agent" });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem("No connected agent advertises RunProcess. Start TOLIK.Agent first.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem($"Agent failed to open '{appName}': {ex.Message}", statusCode: 500);
            }
        }
        case ChatIntentKind.BuildProject:
        case ChatIntentKind.TestProject:
        {
            var projectPath = intent.Argument.Trim();
            if (projectPath.Length == 0 || projectPath.Length > 512) return Results.BadRequest(new { error = "invalid_project_path" });
            var capability = intent.Kind == ChatIntentKind.BuildProject ? AgentCapability.RunBuild : AgentCapability.RunTests;
            var label = intent.Kind == ChatIntentKind.BuildProject ? "РЎР±РѕcЂРєР°" : "РўРµcc‚c‹";
            try
            {
                var result = await dispatcher.ExecuteAsync(principal.UserId, capability, projectPath, cancellationToken);
                await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"{label} РїcЂРѕРµРєc‚Р° {projectPath}: {result}", $"chat.action.{(intent.Kind == ChatIntentKind.BuildProject ? "run_build" : "run_tests")}", 1, cancellationToken);
                return Results.Ok(new { answer = $"{label} Р·Р°РІРµcЂc€РµРЅР°.\n{result}", action = intent.Kind == ChatIntentKind.BuildProject ? "run_build" : "run_tests", details = projectPath, source = "agent" });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem("No connected agent advertises this capability. Start TOLIK.Agent first.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem($"{label} РЅРµ cѓРґР°Р»Р°ccЊ: {ex.Message}", statusCode: 500);
            }
        }
    }
    if (action.StartsWith("РїРѕРєР°Р¶Рё c„Р°Р№Р»c‹ ", StringComparison.OrdinalIgnoreCase) || action.StartsWith("РїРѕРєР°Р¶Рё cРѕРґРµcЂР¶РёРјРѕРµ ", StringComparison.OrdinalIgnoreCase))
    {
        var marker = action.IndexOf(' ');
        var path = action[(marker + 1)..].Trim();
        if (path.StartsWith("cРѕРґРµcЂР¶РёРјРѕРµ ", StringComparison.OrdinalIgnoreCase)) path = path[11..].Trim();
        string[] entries;
        try { entries = await files.ListDirectoryAsync(path, cancellationToken); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (DirectoryNotFoundException) { return Results.NotFound(new { error = "directory_not_found" }); }
        var answer = string.Join(Environment.NewLine, entries);
        await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK: {answer}", "chat.action.list_directory", 1, cancellationToken);
        return Results.Ok(new { answer, action = "list_directory", source = "core" });
    }
    if (action.StartsWith("cc‚Р°c‚cѓc РїcЂРѕc†РµccР° ", StringComparison.OrdinalIgnoreCase))
    {
        var name = action[17..].Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace)) return Results.BadRequest(new { error = "invalid_process_name" });
        System.Diagnostics.Process[] processes;
        try { processes = System.Diagnostics.Process.GetProcessesByName(name); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_process_name" }); }
        try { var answer = $"name={name}; count={processes.Length}; ids={string.Join(',', processes.Select(p => p.Id))}"; await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK: {answer}", "chat.action.process_status", 1, cancellationToken); return Results.Ok(new { answer, action = "get_process_status", source = "core" }); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    var routeKind = TOLIK.Infrastructure.Models.ChatModelSelector.Pick(action);
    // Remembered ask-policy preference (per user) overrides the coding-kind default.
    var route = router.ResolveWithFallback(routeKind, context.RequestServices.GetRequiredService<TOLIK.Infrastructure.Models.ModelPreferenceStore>().Get(principal.UserId));
    if (route is null) return Results.Problem("No model route configured.", statusCode: 503);
    await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"РџРѕР»cЊР·РѕРІР°c‚РµР»cЊ: {request.Message}", "chat.user", 1, cancellationToken);
    var memories = await durableMemory.SearchAsync(principal.UserId, request.Message, MemoryContextBuilder.MaxItems, cancellationToken);
    var prompt = MemoryContextBuilder.Build(request.Message, memories);
    var learned = await rules.GetTopAsync(principal.UserId, 5, cancellationToken);
    prompt = MemoryContextBuilder.AppendRules(prompt, learned);
    prompt = MemoryContextBuilder.AppendPersona(prompt,
        await personalStore.GetPersonalityAsync(principal.UserId, cancellationToken),
        await personalStore.GetHumorAsync(principal.UserId, cancellationToken),
        await personalStore.GetFamilyLoreAsync(principal.UserId, cancellationToken));
    // Identity preamble (§15): without it the model introduces itself as its
    // base model (observed live: "Я Qwen от Alibaba"). Persona needs a subject.
    prompt = "Ты — Толик, домашний AI-ассистент. Отвечай как Толик. " + prompt;
    var raw = await completion.CompleteAsync(route, new ModelRequest(route.Model, prompt, NumPredict: routeKind == TOLIK.Infrastructure.Models.ModelTaskKind.Simple ? 250 : null, Think: routeKind == TOLIK.Infrastructure.Models.ModelTaskKind.Simple ? false : null, NumCtx: routeKind == TOLIK.Infrastructure.Models.ModelTaskKind.Simple ? 8192 : null), TimeSpan.FromSeconds(120), cancellationToken);
    await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK: {raw}", "chat.assistant", 0.8, cancellationToken);
    return Results.Ok(new { answer = raw, model = route.Model, action = "model_response", details = route.Model, source = "model" });
});
app.MapPost("/voice/process", async (VoiceHttpRequest request, HttpContext context, IdentityService identity, IVoiceService voice, VoiceRuntimeGate tts, VoiceProfile profile, ModelRouter router, ModelCompletionClient completion, DurableMemoryStore durableMemory, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    if (request.Transcription is null || !request.Transcription.IsValid) return Results.BadRequest(new { error = "invalid_transcription" });
    var voiceResponse = await voice.ProcessAsync(new VoiceRequest(request.Transcription, request.Speaker, request.Mode), cancellationToken);
    await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"РџРѕР»cЊР·РѕРІР°c‚РµР»cЊ (voice): {voiceResponse.Text}", "voice.user", 1, cancellationToken);
    var route = router.Resolve(ModelTaskKind.Simple);
    if (route is null) return Results.Problem("No simple model route configured.", statusCode: 503);
    string raw;
    try
    {
        raw = await completion.CompleteAsync(route, new ModelRequest(route.Model, voiceResponse.Text, NumPredict: 250, Think: false, NumCtx: 8192), TimeSpan.FromSeconds(120), cancellationToken);
    }
    catch (ModelProviderUnavailableException)
    {
        return Results.Problem("Voice model provider is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK (voice): {raw}", "voice.assistant", 0.8, cancellationToken);
    var audio = await tts.SynthesizeIfEnabledAsync(new TtsRequest(raw, profile, voiceResponse.Mode), request.RuntimeMode, cancellationToken);
    return Results.Ok(new { text = raw, mode = voiceResponse.Mode, audio = audio is null ? null : Convert.ToBase64String(audio.Data), contentType = audio?.ContentType, model = route.Model });
});
app.MapPost("/voice/transcribe", async (HttpContext context, IdentityService identity, WhisperSttProvider stt, DurableMemoryStore durableMemory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);

    // Audio arrives as raw body (wav/mp2). Bound to WhisperOptions.MaxAudioBytes by the provider.
    using var buffer = new MemoryStream();
    try
    {
        await context.Request.Body.CopyToAsync(buffer, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }

    SttResult result;
    try
    {
        result = await stt.TranscribeAsync(buffer.ToArray(), cancellationToken);
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "invalid_audio" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem("Transcription failed: " + ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"РџРѕР»cЊР·РѕРІР°c‚РµР»cЊ (stt): {result.Text}", "voice.stt", 1, cancellationToken);
    return Results.Ok(new { text = result.Text, language = result.Language, durationSeconds = result.DurationSeconds, model = result.Model });
});
app.MapPost("/voice/turn", async (VoiceTurnRequest request, HttpContext context, IdentityService identity, WhisperSttProvider stt, VoiceRuntimeGate tts, VoiceProfile profile, ModelRouter router, ModelCompletionClient completion, DurableMemoryStore durableMemory, LearnedRuleStore rules, PersonalContextStore personalStore, AuditWriter audit, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (request.Audio is null || request.Audio.Length == 0) return Results.BadRequest(new { error = "audio_required" });

    // 1. STT
    SttResult transcription;
    try
    {
        transcription = await stt.TranscribeAsync(request.Audio, cancellationToken);
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "invalid_audio" });
    }
    catch (InvalidOperationException)
    {
        return Results.Problem("Voice transcription is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Guard: empty/blank transcription must never reach memory search (null query crash).
    if (string.IsNullOrWhiteSpace(transcription.Text))
        return Results.Ok(new VoiceTurnResponse("(пауза)", "Я ничего не услышал — повтори, пожалуйста.", VoiceMode.Normal, null, null, transcription.Model));

    // 1b. Speaker check (§13): the same audio, embedding vs enrolled profile.
    // No profile -> device auth only (today's behavior). Profile + mismatch ->
    // guest-mode prompt + provenance flag, never a silent pass.
    var speakerProfiles = await durableMemory.SearchBySourceAsync(principal.UserId, "speaker.profile", cancellationToken);
    var hasSpeakerProfile = speakerProfiles.Count > 0;
    var speakerVerified = false;
    var speakerConfidence = 0.0;
    if (hasSpeakerProfile)
    {
        var turnEmbedding = await EmbedSpeakerAudioAsync(request.Audio, "turn.wav", configuration, context.RequestServices, cancellationToken);
        if (turnEmbedding is not null && turnEmbedding.Count > 0)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(speakerProfiles[0].Content);
                var referenceVector = document.RootElement.GetProperty("embedding").EnumerateArray().Select(e => e.GetDouble()).ToList();
                speakerConfidence = SpeakerVerifier.Cosine(referenceVector, turnEmbedding);
                speakerVerified = SpeakerVerifier.Accept(speakerConfidence);
            }
            catch (ArgumentException) { /* corrupt profile or mismatch: stays unverified */ }
        }
        await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "voice.speaker.turn", speakerVerified ? "ALLOW" : "DENY", speakerVerified ? "speaker_accepted" : "speaker_rejected", principal, null, null, null, speakerVerified ? "accepted" : "rejected", cancellationToken);
    }

    // 2. Memory: store what the user said
    var userSource = hasSpeakerProfile && !speakerVerified ? "voice.user.unverified" : "voice.user";
    await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"РџРѕР»cЊР·РѕРІР°c‚РµР»cЊ (voice): {transcription.Text}", userSource, 1, cancellationToken);

    // 3. Reason вЂ” with intent routing: voice intents must execute deterministically;
    //    the model must never pretend to have stored a memory it cannot store (В§24).
    var intent = IntentRouter.Classify(transcription.Text);
    if (intent.Kind is ChatIntentKind.OpenApp or ChatIntentKind.SwitchVoiceMode or ChatIntentKind.Reminder
        or ChatIntentKind.StoreMemory or ChatIntentKind.StoreFamilyMeme or ChatIntentKind.StoreFunny
        or ChatIntentKind.RecallMemory)
    {
        var voiceChat = await ProcessSimpleIntentAsync(principal, intent, context, durableMemory, cancellationToken);
        if (voiceChat is not null) return voiceChat;
    }

    var voiceKind = TOLIK.Infrastructure.Models.ChatModelSelector.Pick(transcription.Text);
    // Remembered ask-policy preference (per user) overrides the coding-kind default.
    var route = router.ResolveWithFallback(voiceKind, context.RequestServices.GetRequiredService<TOLIK.Infrastructure.Models.ModelPreferenceStore>().Get(principal.UserId));
    if (route is null) return Results.Problem("No model route configured.", statusCode: 503);
    string raw;
    try
    {
        var memories = await durableMemory.SearchAsync(principal.UserId, transcription.Text, MemoryContextBuilder.MaxItems, cancellationToken);
        var prompt = MemoryContextBuilder.BuildVoice(transcription.Text, memories);
        var learned = await rules.GetTopAsync(principal.UserId, 5, cancellationToken);
        prompt = MemoryContextBuilder.AppendRules(prompt, learned);
        // Voice budget: tone/style only (one line); humor and lore stay in chat.
        // Skipping those two queries keeps voice turns within the ~3s norm.
        prompt = MemoryContextBuilder.AppendPersona(prompt,
            await personalStore.GetPersonalityAsync(principal.UserId, cancellationToken),
            [], [],
            concise: true);
        prompt = MemoryContextBuilder.AppendSpeakerContext(prompt, hasSpeakerProfile, speakerVerified, speakerConfidence);
        raw = await completion.CompleteAsync(route, new ModelRequest(route.Model, prompt, NumPredict: voiceKind == TOLIK.Infrastructure.Models.ModelTaskKind.Simple ? 250 : null, Think: voiceKind == TOLIK.Infrastructure.Models.ModelTaskKind.Simple ? false : null, NumCtx: voiceKind == TOLIK.Infrastructure.Models.ModelTaskKind.Simple ? 8192 : null), TimeSpan.FromSeconds(120), cancellationToken);
    }
    catch (ModelProviderUnavailableException)
    {
        return Results.Problem("Voice model provider is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // 4. Memory: store what TOLIK said
    await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK (voice): {raw}", "voice.assistant", 0.8, cancellationToken);

    // 5. TTS вЂ” global runtime mode applies unless the request carries an explicit one.
    // Speech cut: long monologues are trimmed to the last full sentence (~30s of audio).
    var speech = MemoryContextBuilder.TrimForSpeech(raw);
    var runtimeState = context.RequestServices.GetRequiredService<VoiceRuntimeState>();
    var effectiveMode = request.RuntimeMode == VoiceRuntimeMode.Normal && runtimeState.Current != VoiceRuntimeMode.Normal
        ? runtimeState.Current
        : request.RuntimeMode;
    var audio = await tts.SynthesizeIfEnabledAsync(new TtsRequest(speech, profile, VoiceMode.Normal), effectiveMode, cancellationToken);
    return Results.Ok(new VoiceTurnResponse(transcription.Text, raw, VoiceMode.Normal, audio?.Data, audio?.ContentType, route.Model, speakerVerified, speakerConfidence));
});
app.MapPost("/voice/speaker/enroll", async (HttpContext context, IdentityService identity, IConfiguration configuration, DurableMemoryStore durableMemory, AuditWriter audit, CancellationToken cancellationToken) =>
{
    // Owner voice enrollment (§13): sample -> voice-service embedding -> private
    // memory profile. Re-enrollment replaces the previous profile. Audio never
    // persists: only the embedding is stored, scoped Private to the owner.
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (!context.Request.HasFormContentType || context.Request.Form.Files.Count == 0)
        return Results.BadRequest(new { error = "audio_required" });
    // Multi-sample enrollment (up to 5 clips): the stored profile is the mean
    // unit vector — far more stable than a single short sample (live: single
    // short clips of the same speaker scored as low as 0.43).
    var vectors = new List<List<double>>();
    foreach (var file in context.Request.Form.Files.Take(5))
    {
        var sample = await EmbedSpeakerSampleAsync(file, configuration, context.RequestServices, cancellationToken);
        if (sample is null)
            return Results.Json(new { error = "speaker_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        if (sample.Count > 0) vectors.Add(sample);
    }
    if (vectors.Count == 0)
        return Results.BadRequest(new { error = "audio_too_short" });
    var dim = vectors[0].Count;
    if (vectors.Any(v => v.Count != dim))
        return Results.BadRequest(new { error = "embedding_mismatch" });
    var mean = new double[dim];
    foreach (var vector in vectors)
        for (var i = 0; i < dim; i++) mean[i] += vector[i] / vectors.Count;
    var norm = Math.Sqrt(mean.Sum(x => x * x));
    if (norm <= 0.0) return Results.BadRequest(new { error = "audio_too_short" });
    var embedding = mean.Select(x => x / norm).ToList();
    foreach (var existing in await durableMemory.SearchBySourceAsync(principal.UserId, "speaker.profile", cancellationToken))
        await durableMemory.DeleteAsync(principal.UserId, existing.Id, cancellationToken);
    var stored = System.Text.Json.JsonSerializer.Serialize(new
    {
        embedding,
        dim = embedding.Count,
        model = "campplus",
        enrolledAt = DateTimeOffset.UtcNow,
    });
    await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, stored, "speaker.profile", 1.0, cancellationToken);
    await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "voice.speaker.enroll", "ALLOW", "speaker_enrolled", principal, null, null, null, "enrolled", cancellationToken);
    return Results.Ok(new { enrolled = true, dim = embedding.Count });
});
app.MapPost("/voice/speaker/verify", async (HttpContext context, IdentityService identity, IConfiguration configuration, DurableMemoryStore durableMemory, AuditWriter audit, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (!context.Request.HasFormContentType || context.Request.Form.Files.Count == 0)
        return Results.BadRequest(new { error = "audio_required" });
    var profiles = await durableMemory.SearchBySourceAsync(principal.UserId, "speaker.profile", cancellationToken);
    var reference = profiles.FirstOrDefault();
    if (reference is null) return Results.NotFound(new { error = "needs_enrollment" });
    var embedding = await EmbedSpeakerSampleAsync(context.Request.Form.Files[0], configuration, context.RequestServices, cancellationToken);
    if (embedding is null)
        return Results.Json(new { error = "speaker_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (embedding.Count == 0)
        return Results.BadRequest(new { error = "audio_too_short" });
    List<double> referenceVector;
    try
    {
        using var document = System.Text.Json.JsonDocument.Parse(reference.Content);
        referenceVector = document.RootElement.GetProperty("embedding").EnumerateArray().Select(e => e.GetDouble()).ToList();
    }
    catch (Exception)
    {
        return Results.Json(new { error = "speaker_profile_corrupt" }, statusCode: StatusCodes.Status500InternalServerError);
    }
    double similarity;
    try { similarity = SpeakerVerifier.Cosine(referenceVector, embedding); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "embedding_mismatch" }); }
    var verified = SpeakerVerifier.Accept(similarity);
    await audit.WriteAuthorizationAsync((Guid)context.Items["CorrelationId"]!, "voice.speaker.verify", verified ? "ALLOW" : "DENY", verified ? "speaker_accepted" : "speaker_rejected", principal, null, null, null, verified ? "accepted" : "rejected", cancellationToken);
    return Results.Ok(new { verified, confidence = similarity, subject = principal.UserId.ToString("D"), provider = "campplus" });
});
static async Task<List<double>?> EmbedSpeakerSampleAsync(Microsoft.AspNetCore.Http.IFormFile file, IConfiguration configuration, IServiceProvider services, CancellationToken cancellationToken)
{
    if (file is null) return new List<double>();
    using var buffer = new MemoryStream();
    await file.CopyToAsync(buffer, cancellationToken);
    return await EmbedSpeakerAudioAsync(buffer.ToArray(), file.FileName, configuration, services, cancellationToken);
}
static async Task<List<double>?> EmbedSpeakerAudioAsync(byte[] audio, string fileName, IConfiguration configuration, IServiceProvider services, CancellationToken cancellationToken)
{
    // Returns null when verification is disabled, empty list when the sample is
    // too short — the two failure modes map to 503 and 400 respectively.
    if (audio is null || audio.Length == 0 || audio.Length > 10 * 1024 * 1024) return new List<double>();
    var voiceBase = configuration["TOLIK_VOICE:COSYVOICE_ENDPOINT"] ?? "http://voice:9660";
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
    http.Timeout = TimeSpan.FromSeconds(30);
    using var content = new MultipartFormDataContent { { new ByteArrayContent(audio), "file", fileName } };
    System.Net.Http.HttpResponseMessage response;
    try { response = await http.PostAsync(voiceBase.TrimEnd('/') + "/voice/speaker/embed", content, cancellationToken); }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    catch { return null; }
    if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable) return null;
    if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity) return new List<double>();
    if (!response.IsSuccessStatusCode) return null;
    try
    {
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("embedding").EnumerateArray().Select(e => e.GetDouble()).ToList();
    }
    catch { return null; }
}
// WebSocket realtime events (В§29). app.Map branch: the delegate completes the
// socket itself, so no IResult is returned after the handshake.
app.MapGet("/events/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "websocket_required" });
        return;
    }
    var token = context.Request.Query["token"].ToString();
    var identity = context.RequestServices.GetRequiredService<IdentityService>();
    var principal = await identity.AuthenticateAsync(token, context.RequestAborted);
    if (principal is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "authentication_required" });
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = Guid.NewGuid();
    realtime.Register(connectionId, principal.UserId, socket);
    try
    {
        var hello = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"userId\":\"" + principal.UserId.ToString("D") + "\"}");
        await socket.SendAsync(hello, WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);
        var receiveBuffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(receiveBuffer, context.RequestAborted);
            if (received.MessageType == WebSocketMessageType.Close) break;
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (WebSocketException) { }
    finally { realtime.Unregister(connectionId); }
});
// Agent WebSocket endpoint for command push (avoids HTTP polling head-of-line blocking)
app.MapGet("/agents/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "websocket_required" });
        return;
    }
    var token = context.Request.Query["token"].ToString();
    var identity = context.RequestServices.GetRequiredService<IdentityService>();
    var principal = await identity.AuthenticateAsync(token, context.RequestAborted);
    if (principal is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "authentication_required" });
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = Guid.NewGuid();

    // Register agent WebSocket connection. The agent identifies itself with
    // ?agentId= so the socket binds to the right entry when one owner runs
    // several agents; the id must be owned by the caller (fail closed).
    var registry = context.RequestServices.GetRequiredService<AgentConnectionRegistry>();
    var requested = context.Request.Query["agentId"].ToString();
    Guid? requestedAgentId = Guid.TryParse(requested, out var parsedAgentId) ? parsedAgentId : null;
    var agentRegistered = registry.RegisterWebSocket(connectionId, principal.UserId, socket, requestedAgentId);
    if (!agentRegistered)
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "agent_not_owned", CancellationToken.None);
        return;
    }

    // Send hello
    var hello = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"userId\":\"" + principal.UserId.ToString("D") + "\",\"agent\":true}");
    await socket.SendAsync(hello, WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);

    var receiveBuffer = new byte[4096];
    try
    {
        // Periodic ping to keep WebSocket alive (every 30 seconds, serialized send).
        var pingTimer = new System.Threading.Timer(async _ =>
        {
            if (socket.State == WebSocketState.Open)
                await registry.SendPingAsync(connectionId, CancellationToken.None);
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(receiveBuffer, context.RequestAborted);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                // Handle agent responses (command completions); pong/heartbeat refresh LastSeen.
                if (received.MessageType == WebSocketMessageType.Text)
                {
                    var json = System.Text.Encoding.UTF8.GetString(receiveBuffer, 0, received.Count);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var typeProp)) continue;
                        var msgType = typeProp.GetString();
                        if (msgType == "command_response")
                        {
                            var cmdId = Guid.Parse(root.GetProperty("commandId").GetString()!);
                            var outcome = root.GetProperty("outcome").GetInt32();
                            var result = root.TryGetProperty("result", out var r) ? r.GetString() : null;
                            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                            var response = new AgentCommandResponse(cmdId, Guid.Empty, (CommandOutcome)outcome, result, error);
                            registry.CompleteWebSocket(connectionId, principal.UserId, response);
                        }
                        else if (msgType == "pong" || msgType == "heartbeat")
                        {
                            registry.TouchWebSocket(connectionId, principal.UserId);
                        }
                    }
                    catch { }
                }
            }
        }
        finally
        {
            pingTimer?.Dispose();
            registry.UnregisterWebSocket(connectionId);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (WebSocketException) { }
    catch (Exception) { }
});
app.MapGet("/agents", async (HttpContext context, IdentityService identity, AgentConnectionRegistry registry, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    return principal is null ? AuthenticationRequired(context) : Results.Ok(registry.GetForUser(principal.UserId));
});
app.MapPost("/proactive/remind", async (ProactiveRemindRequest request, HttpContext context, IdentityService identity, ProactiveStore proactive, DurableMemoryStore durableMemory, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (request is null || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2048) return Results.BadRequest(new { error = "invalid_reminder" });
    DateTimeOffset? dueAt = request.DelaySeconds is > 0 and <= 60 * 60 * 24 * 30
        ? DateTimeOffset.UtcNow.AddSeconds(request.DelaySeconds.Value)
        : null;
    if (dueAt is null) return Results.BadRequest(new { error = "invalid_delay", detail = "DelaySeconds must be between 1 and 2592000 (30 days)." });
    try
    {
        var scheduled = await proactive.ScheduleAsync(principal.UserId, request.Text, request.Priority ?? "Normal", dueAt.Value, request.Reason ?? "user_reminder", cancellationToken);
        await durableMemory.AddAsync(principal.UserId, MemoryScope.Private, $"TOLIK Р·Р°РїРёcР°Р» РЅР°РїРѕРјРёРЅР°РЅРёРµ: {request.Text} (РЅР° {dueAt:HH:mm})", "proactive.reminder", 1, cancellationToken);
        return Results.Ok(new { id = scheduled.Id, text = scheduled.Text, dueAt = scheduled.DueAt, priority = scheduled.Priority });
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "invalid_reminder" });
    }
});
app.MapGet("/proactive/poll", async (HttpContext context, IdentityService identity, ProactiveStore proactive, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var due = await proactive.TakeDueAsync(principal.UserId, 8, cancellationToken);
    return Results.Ok(due.Select(x => new { id = x.Id, text = x.Text, priority = x.Priority, dueAt = x.DueAt, reason = x.Reason }));
});
app.MapGet("/proactive/pending", async (HttpContext context, IdentityService identity, ProactiveStore proactive, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    var pending = await proactive.ListPendingAsync(principal.UserId, cancellationToken);
    return Results.Ok(pending.Select(x => new { id = x.Id, text = x.Text, priority = x.Priority, dueAt = x.DueAt }));
});
app.MapPost("/proactive/{messageId:guid}/cancel", async (Guid messageId, HttpContext context, IdentityService identity, ProactiveStore proactive, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    return await proactive.CancelAsync(principal.UserId, messageId, cancellationToken)
        ? Results.Ok(new { cancelled = true })
        : Results.NotFound(new { error = "reminder_not_found" });
});
app.MapPost("/mfa/enroll", async (MfaEnrollRequest request, HttpContext context, IdentityService identity, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (request is null || request.DeviceId == Guid.Empty) return Results.BadRequest(new { error = "device_id_required" });
    try
    {
        var result = await identity.EnrollMfaAsync(request.DeviceId, cancellationToken);
        return Results.Ok(new { secret = result.Secret, qrCodeUri = result.QrCodeUri });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem("MFA enrollment failed: " + ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});
app.MapPost("/mfa/verify", async (MfaVerifyRequest request, HttpContext context, IdentityService identity, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    if (request is null || string.IsNullOrWhiteSpace(request.Code) || request.DeviceId == Guid.Empty)
        return Results.BadRequest(new { error = "code_and_device_required" });
    var result = await identity.VerifyMfaAsync(request.DeviceId, request.Code, cancellationToken);
    if (result.Success)
        return Results.Ok(new { verified = true });
    return Results.BadRequest(new { error = result.Error });
});
app.MapGet("/mfa/status", async (HttpContext context, IdentityService identity, CancellationToken cancellationToken) =>
{
    var principal = await AuthenticateRequestAsync(context, identity, cancellationToken);
    if (principal is null) return AuthenticationRequired(context);
    return Results.Ok(new { enrolled = false });
});
app.MapGet("/status/modules", async (HttpContext context, IdentityService identity, AgentConnectionRegistry registry, ILocalVoiceRuntime voiceRuntime, WhisperSttProvider stt, CancellationToken cancellationToken) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var principal = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? await identity.AuthenticateAsync(header[7..], cancellationToken) : null;
    if (principal is null) return AuthenticationRequired(context);
    var voice = await voiceRuntime.CheckHealthAsync(cancellationToken);
    var sttHealth = await stt.CheckHealthAsync(cancellationToken);
    return Results.Ok(new { core = "Healthy", memory = "PostgresBacked", voice = voice.Status, voiceAvailable = voice.Available, stt = sttHealth.Status, sttAvailable = sttHealth.Available, agents = registry.GetForUser(principal.UserId).Count });
});

app.Run();

public sealed record BootstrapChallengeRequest(string? BootstrapCode);
public sealed record BootstrapRequest(string? BootstrapCode, string InstallationId, string PublicKey, string? Challenge, string? Signature);
public sealed record FileToolRequest(string Path);
public sealed record PlanRequest(string? Request, int MaxSteps = 8, int BudgetSeconds = 300, string[]? Steps = null, bool AutoPlan = false);
public sealed record MemoryRequest(MemoryScope Scope, string? Content, string? Source, double Confidence = 1, string? Origin = null);
public sealed record PersonRequest(string? Name, string? Relationship, MemoryScope Scope = MemoryScope.Shared);
public sealed record HumorRequest(MemoryScope Scope, string? Content, string? Source);
public sealed record PersonalityRequest(string? Tone, string? Style, bool UseHumor = true);
public sealed record FamilyLoreRequest(string? Content);
public sealed record RelationshipRequest(MemoryScope Scope, Guid PersonId, string? Relation);
public sealed record PersonalEventRequest(MemoryScope Scope, string? Type, string? Content, DateTimeOffset? OccurredAt);
public sealed record ChatRequest(string? Message);
public sealed record VoiceHttpRequest(VoiceTranscription? Transcription, SpeakerVerification? Speaker, VoiceMode Mode = VoiceMode.Normal, VoiceRuntimeMode RuntimeMode = VoiceRuntimeMode.Normal);
public sealed record ProactiveRemindRequest(string? Text, int? DelaySeconds, string? Priority, string? Reason);
public sealed record TaskFeedbackRequest(bool Positive, string? Comment);
public sealed record PlaySoundRequest(string? Category, string? Name);
public sealed record MfaEnrollRequest(Guid DeviceId);
public sealed record MfaVerifyRequest(Guid DeviceId, string Code);
public sealed record LockRequest(string? Reason);
public sealed record UnlockRequest(string? UnlockCode);
public sealed record ModelPreferenceRequest(string? Mode);

public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, (AgentRegistration Registration, Guid UserId, DateTimeOffset ConnectedAt)> connections = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<AgentCommandEnvelope>> queues = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> queueSignals = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AgentCommandResponse>> pending = new();
    private readonly ConcurrentDictionary<Guid, Guid> pendingAgents = new();
    // WebSocket connections for agents (connectionId -> (agentId, userId, socket))
    private readonly ConcurrentDictionary<Guid, (Guid AgentId, Guid UserId, WebSocket Socket)> wsConnections = new();
    // One send gate per WS connection: ClientWebSocket/ASP.NET sockets allow a single
    // outstanding SendAsync; concurrent sends tear the connection down (long-idle drops).
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> wsSendGates = new();
    private readonly object connectionGate = new();
    
    public bool Connect(AgentRegistration registration, TOLIK.Infrastructure.Identity.SessionPrincipal principal)
    {
        lock (connectionGate)
        {
            if (connections.TryGetValue(registration.AgentId, out var current) && current.UserId != principal.UserId) return false;
            connections[registration.AgentId] = (registration, principal.UserId, DateTimeOffset.UtcNow);
            queues.TryAdd(registration.AgentId, new ConcurrentQueue<AgentCommandEnvelope>());
            queueSignals.TryAdd(registration.AgentId, new SemaphoreSlim(0));
            return true;
        }
    }
    
    public bool Disconnect(Guid agentId, Guid userId)
    {
        lock (connectionGate)
        {
            if (!connections.TryGetValue(agentId, out var current) || current.UserId != userId) return false;
            connections.TryRemove(agentId, out _);
            if (queues.TryRemove(agentId, out var queue))
            {
                if (queueSignals.TryRemove(agentId, out var signal)) signal.Release();
            }
            foreach (var pendingCommand in pendingAgents.Where(item => item.Value == agentId).Select(item => item.Key).ToArray())
            {
                pendingAgents.TryRemove(pendingCommand, out _);
                if (pending.TryRemove(pendingCommand, out var completion)) completion.TrySetCanceled(new CancellationToken(true));
            }
            return true;
        }
    }
    
    public bool Touch(Guid agentId, Guid userId)
    {
        lock (connectionGate)
        {
            if (!connections.TryGetValue(agentId, out var current) || current.UserId != userId) return false;
            connections[agentId] = (current.Registration, current.UserId, DateTimeOffset.UtcNow);
            return true;
        }
    }
    
    public bool Owns(Guid agentId, Guid userId)
    {
        lock (connectionGate) return connections.TryGetValue(agentId, out var current) && current.UserId == userId;
    }
    
    public async Task<AgentCommandResponse> DispatchRemoteAsync(Guid agentId, AgentCapability capability, string argument, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var command = new AgentCommandEnvelope(Guid.NewGuid(), agentId, capability, argument, DateTimeOffset.UtcNow);
        var completion = new TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Register completion FIRST so a fast agent response can't get lost
        lock (connectionGate)
        {
            if (!connections.ContainsKey(agentId) || !queues.ContainsKey(agentId) ||
                !pending.TryAdd(command.CommandId, completion) || !pendingAgents.TryAdd(command.CommandId, agentId))
            {
                throw new InvalidOperationException("Agent command queue is unavailable.");
            }
        }

        // Try WebSocket first (instant); fall back to HTTP queue only when WS is down.
        // A command must live in exactly one channel to avoid double execution.
        bool sentViaWs = false;
        bool knownAgent;
        lock (connectionGate) knownAgent = connections.ContainsKey(agentId) && queues.ContainsKey(agentId);
        for (int attempt = 0; attempt < 6 && knownAgent; attempt++)
        {
            sentViaWs = await TrySendViaWebSocketAsync(agentId, command, cancellationToken);
            if (sentViaWs) break;

            // Brief delay before retry to allow agent reconnect
            if (attempt < 5) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        if (!sentViaWs)
        {
            // Fall back to HTTP polling queue
            lock (connectionGate)
            {
                if (queues.TryGetValue(agentId, out var httpQueue))
                {
                    httpQueue.Enqueue(command);
                    if (queueSignals.TryGetValue(agentId, out var httpSignal)) httpSignal.Release();
                }
            }
        }
        
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var registration = timeoutSource.Token.Register(() => completion.TrySetCanceled(timeoutSource.Token));
        try
        {
            return await completion.Task;
        }
        finally
        {
            pending.TryRemove(command.CommandId, out _);
            pendingAgents.TryRemove(command.CommandId, out _);
        }
    }
    
    public bool Complete(Guid agentId, Guid userId, AgentCommandResponse response)
    {
        lock (connectionGate)
        {
            var hasConnection = connections.TryGetValue(agentId, out var current);
            var hasPending = pending.TryGetValue(response.CommandId, out var completion);
            if (!hasConnection || current.UserId != userId || response.AgentId != agentId || !hasPending) return false;
            var result = completion.TrySetResult(response);
            Console.WriteLine($"[CORE COMPLETE] Agent {agentId}, cmd {response.CommandId}, outcome {response.Outcome}, accepted: {result}");
            return result;
        }
    }
    
    public Task<(bool Owned, AgentCommandEnvelope? Command)> PollAsync(Guid agentId, Guid userId, CancellationToken cancellationToken)
    {
        ConcurrentQueue<AgentCommandEnvelope> pollQueue;
        SemaphoreSlim pollSignal;
        lock (connectionGate)
        {
            if (!connections.TryGetValue(agentId, out var current)) 
            {
                return Task.FromResult<(bool Owned, AgentCommandEnvelope? Command)>((false, null));
            }
            if (current.UserId != userId) 
            {
                return Task.FromResult<(bool Owned, AgentCommandEnvelope? Command)>((false, null));
            }
            if (!queues.TryGetValue(agentId, out pollQueue)) return Task.FromResult<(bool Owned, AgentCommandEnvelope? Command)>((false, null));
            if (!queueSignals.TryGetValue(agentId, out pollSignal)) return Task.FromResult<(bool Owned, AgentCommandEnvelope? Command)>((false, null));
        }
        // Short-poll: single TryDequeue, no semaphore wait (agent polls every second).
        if (queues.TryGetValue(agentId, out var pollQ) && pollQ.TryDequeue(out var command))
        {
            Console.WriteLine($"[CORE POLL] Agent {agentId}, dequeued command: {command.CommandId}");
            return Task.FromResult<(bool Owned, AgentCommandEnvelope? Command)>((true, command));
        }
        return Task.FromResult<(bool Owned, AgentCommandEnvelope? Command)>((true, null));
    }
    
    // Register agent WebSocket connection. With an explicit agent id the socket
    // binds exactly (ownership verified); without it, fall back to the owner's
    // entry for backward compatibility with older agents.
    public bool RegisterWebSocket(Guid connectionId, Guid userId, WebSocket socket, Guid? requestedAgentId = null)
    {
        lock (connectionGate)
        {
            Guid agentId;
            if (requestedAgentId.HasValue)
            {
                if (!connections.TryGetValue(requestedAgentId.Value, out var owned) || owned.UserId != userId) return false;
                agentId = requestedAgentId.Value;
            }
            else
            {
                // Find agentId for this user that has a WebSocket connection
                var agentEntry = connections.Values.FirstOrDefault(x => x.UserId == userId);
                if (agentEntry.Registration.AgentId == Guid.Empty) return false;
                agentId = agentEntry.Registration.AgentId;
            }
            
            // Drop stale entries for the same agent (reconnects), then store
            foreach (var staleId in wsConnections.Where(kvp => kvp.Value.AgentId == agentId && kvp.Key != connectionId).Select(kvp => kvp.Key).ToArray())
                wsConnections.TryRemove(staleId, out _);
            wsConnections[connectionId] = (agentId, userId, socket);
            return true;
        }
    }
    
    public void UnregisterWebSocket(Guid connectionId)
    {
        lock (connectionGate)
        {
            wsConnections.TryRemove(connectionId, out _);
        }
        if (wsSendGates.TryRemove(connectionId, out var gate)) gate.Dispose();
    }

    /// <summary>Refresh agent LastSeen from any WS activity (pong/heartbeat).</summary>
    public void TouchWebSocket(Guid connectionId, Guid userId)
    {
        Guid agentId;
        lock (connectionGate)
        {
            if (!wsConnections.TryGetValue(connectionId, out var ws) || ws.UserId != userId) return;
            agentId = ws.AgentId;
        }
        Touch(agentId, userId);
    }

    public async Task SendPingAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim? gate = null;
        bool acquired = false;
        try
        {
            if (!wsSendGates.TryGetValue(connectionId, out gate))
                gate = wsSendGates.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
            WebSocket? socket;
            lock (connectionGate) socket = wsConnections.TryGetValue(connectionId, out var ws) ? ws.Socket : null;
            if (socket is null || socket.State != WebSocketState.Open) return;
            await gate.WaitAsync(cancellationToken);
            acquired = true;
            if (socket.State != WebSocketState.Open) return;
            var ping = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
            await socket.SendAsync(ping, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { }
        finally { if (acquired && gate is not null) { try { gate.Release(); } catch { } } }
    }
    
    // Complete a command via WebSocket
    public bool CompleteWebSocket(Guid connectionId, Guid userId, AgentCommandResponse response)
    {
        lock (connectionGate)
        {
            if (!wsConnections.TryGetValue(connectionId, out var ws) || ws.UserId != userId) return false;
            // Complete the pending command
            if (pending.TryGetValue(response.CommandId, out var completion))
            {
                pendingAgents.TryRemove(response.CommandId, out _);
                return completion.TrySetResult(response);
            }
            return false;
        }
    }
    
    // Send command via WebSocket if available, otherwise queue for HTTP polling.
    // Sends are serialized per connection and executed outside connectionGate:
    // holding a lock across a blocking send deadlocks Poll/Complete and concurrent
    // SendAsync calls tear the socket down.
    private async Task<bool> TrySendViaWebSocketAsync(Guid agentId, AgentCommandEnvelope command, CancellationToken cancellationToken)
    {
        (Guid ConnId, WebSocket Socket)[] targets;
        lock (connectionGate)
        {
            targets = wsConnections.Where(kvp => kvp.Value.AgentId == agentId && kvp.Value.Socket.State == WebSocketState.Open)
                .Select(kvp => (kvp.Key, kvp.Value.Socket)).ToArray();
            if (targets.Length == 0) return false;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "command",
            commandId = command.CommandId,
            capability = command.Capability.ToString(),
            argument = command.Argument,
            createdAt = command.CreatedAt
        });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        foreach (var (connId, socket) in targets)
        {
            var gate = wsSendGates.GetOrAdd(connId, _ => new SemaphoreSlim(1, 1));
            bool acquired = false;
            try
            {
                await gate.WaitAsync(cancellationToken);
                acquired = true;
                if (socket.State != WebSocketState.Open)
                {
                    wsConnections.TryRemove(connId, out _);
                    continue;
                }
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WS] Send failed, pruning connection: {ex.Message}");
                wsConnections.TryRemove(connId, out _);
            }
            finally { if (acquired) { try { gate.Release(); } catch { } } }
        }
        return false;
    }
    
    public IReadOnlyList<AgentConnection> GetForUser(Guid userId)
    {
        lock (connectionGate)
        {
            return connections.Values.Where(x => x.UserId == userId)
                .Select(x => new AgentConnection(x.Registration.AgentId, x.Registration.AdvertisedCapabilities, x.ConnectedAt))
                .ToArray();
        }
    }
}

public sealed record AgentConnection(Guid AgentId, IReadOnlySet<AgentCapability> Capabilities, DateTimeOffset LastSeen)
{
    public bool IsStale => DateTimeOffset.UtcNow - LastSeen > TimeSpan.FromMinutes(10);
    public string Status => IsStale ? "Stale" : "Connected";
}
public sealed record TaskRequest(string? Description);
public sealed record AgentHeartbeatRequest(Guid AgentId);

/// <summary>Control-flow for a failed plan step: stop without cascading, report partial results.</summary>
public sealed class StepFailedException(string message, Exception? inner = null) : Exception(message, inner);


