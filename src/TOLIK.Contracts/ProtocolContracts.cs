using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TOLIK.Contracts;

public readonly record struct ProtocolVersion(string Value);

public static class ProtocolVersions
{
    public static ProtocolVersion V1 { get; } = new("v1");

    public static bool IsSupported(ProtocolVersion version) => version == V1;
}

public enum IdentityRole
{
    Guest,
    TrustedGuest,
    Family,
    AuthorizedUser,
    Owner
}

public enum AgentCapability
{
    ReadFile,
    ListDirectory,
    WriteFile,
    RunBuild,
    RunTests,
    GetProcessStatus,
    RunProcess,
    CancelTask,
    RunCodex,
    RunOpenCode,
    PlaySound,
    /// <summary>Captures the screen (CU-1 foundation) — reads, never modifies; privacy-sensitive (§50).</summary>
    CaptureScreen,
    /// <summary>Reads a UI element's value/state by its bounded target spec (CU-6 GUI
    /// actions v1) — READ_ONLY perception; privacy-sensitive (§50: reads the user's
    /// active window — the same trust class as CaptureScreen).</summary>
    GuiRead,
    /// <summary>Invokes a UI element (button/toggle) by its bounded target spec (CU-6
    /// GUI actions v1) — MODIFY: a click changes UI state. Semantic addressing only
    /// (automation_id/name — NEVER coordinates; SkillStore parity).</summary>
    GuiClick,
    /// <summary>Installs a TOLIK self-update (D2 Repair Mode) — CRITICAL: the signed
    /// artifact + the integrity verification + the MFA confirmation, always.</summary>
    InstallTolikUpdate
}

public enum ToolRiskLevel
{
    ReadOnly,
    LowRisk,
    Modify,
    Destructive,
    Critical
}

public static class ConfirmationMethods
{
    public const string ExplicitUserConfirm = "explicit-user-confirm";
    public const string OwnerStrongAuth = "owner-strong-auth";
}

public enum CommandOutcome
{
    Completed,
    Failed,
    Cancelled
}

public enum ProtocolEventKind
{
    TaskStarted,
    TaskProgress,
    ToolStarted,
    ToolCompleted,
    TaskCompleted,
    TaskFailed,
    AgentConnected,
    AgentDisconnected
}

public enum ProtocolValidationErrorCode
{
    MalformedJson,
    MissingProtocolVersion,
    UnknownProtocolVersion,
    MissingCorrelationId,
    MissingRequesterId,
    MissingSessionId,
    MissingDeviceId,
    MissingTaskId,
    MissingCommandId,
    MissingAgentId,
    MissingEventId,
    MissingEventTimestamp,
    UnknownIdentityRole,
    UnknownCapability,
    UnknownRiskLevel,
    UnknownCommandOutcome,
    UnknownEventKind,
    ForbiddenArgumentKey
}

public sealed record ProtocolValidationError(ProtocolValidationErrorCode Code, string Field);

/// <summary>
/// Denied <see cref="AgentCommand.Arguments"/> keys that would smuggle a generic
/// command/shell surface through the explicit-capability protocol.
/// Case-insensitive; shared by contract validation and transport authorization
/// (defense in depth).
/// </summary>
public static class CommandArgumentPolicy
{
    public static readonly FrozenSet<string> DeniedKeys =
        FrozenSet.ToFrozenSet<string>(["command", "shell", "exec", "argv", "script", "eval"], StringComparer.OrdinalIgnoreCase);

    public static bool IsDenied(string key) => key is not null && DeniedKeys.Contains(key);
}

public sealed record ProtocolValidationResult(ImmutableArray<ProtocolValidationError> Errors)
{
    public bool IsValid => Errors.IsEmpty;

    internal static ProtocolValidationResult From(IReadOnlyCollection<ProtocolValidationError> errors) =>
        new([.. errors]);
}

public interface IProtocolPayload
{
    ProtocolValidationResult Validate();
}

public sealed record RequesterIdentity(Guid IdentityId, IdentityRole Role);

public sealed record SessionReference(Guid SessionId, Guid DeviceId);

public sealed record TaskReference(Guid TaskId);

public sealed record AgentCommand(
    Guid CommandId,
    AgentCapability Capability,
    ToolRiskLevel RiskLevel,
    ImmutableDictionary<string, string> Arguments);

public sealed record CommandRequest(
    RequesterIdentity Requester,
    SessionReference Session,
    TaskReference Task,
    AgentCommand Command) : IProtocolPayload
{
    public ProtocolValidationResult Validate()
    {
        var errors = new List<ProtocolValidationError>();

        if (Requester is null)
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "payload.requester"));
        }
        else
        {
            ProtocolValidation.RequireId(Requester.IdentityId, ProtocolValidationErrorCode.MissingRequesterId, "payload.requester.identityId", errors);
            ProtocolValidation.RequireDefined(Requester.Role, ProtocolValidationErrorCode.UnknownIdentityRole, "payload.requester.role", errors);
        }

        if (Session is null)
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "payload.session"));
        }
        else
        {
            ProtocolValidation.RequireId(Session.SessionId, ProtocolValidationErrorCode.MissingSessionId, "payload.session.sessionId", errors);
            ProtocolValidation.RequireId(Session.DeviceId, ProtocolValidationErrorCode.MissingDeviceId, "payload.session.deviceId", errors);
        }

        if (Task is null)
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "payload.task"));
        }
        else
        {
            ProtocolValidation.RequireId(Task.TaskId, ProtocolValidationErrorCode.MissingTaskId, "payload.task.taskId", errors);
        }

        if (Command is null)
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "payload.command"));
        }
        else
        {
            ProtocolValidation.RequireId(Command.CommandId, ProtocolValidationErrorCode.MissingCommandId, "payload.command.commandId", errors);
            ProtocolValidation.RequireDefined(Command.Capability, ProtocolValidationErrorCode.UnknownCapability, "payload.command.capability", errors);
            ProtocolValidation.RequireDefined(Command.RiskLevel, ProtocolValidationErrorCode.UnknownRiskLevel, "payload.command.riskLevel", errors);

            if (Command.Arguments is null)
            {
                errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "payload.command.arguments"));
            }
            else
            {
                foreach (var key in Command.Arguments.Keys)
                {
                    if (CommandArgumentPolicy.IsDenied(key))
                    {
                        errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.ForbiddenArgumentKey, $"payload.command.arguments.{key}"));
                    }
                }
            }
        }

        return ProtocolValidationResult.From(errors);
    }
}

public sealed record AgentRegistration(
    Guid AgentId,
    ImmutableHashSet<AgentCapability> AdvertisedCapabilities,
    string? PublicKey = null) : IProtocolPayload
{
    public ProtocolValidationResult Validate()
    {
        var errors = new List<ProtocolValidationError>();
        ProtocolValidation.RequireId(AgentId, ProtocolValidationErrorCode.MissingAgentId, "payload.agentId", errors);

        if (AdvertisedCapabilities is null)
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "payload.advertisedCapabilities"));
        }
        else
        {
            foreach (var capability in AdvertisedCapabilities)
            {
                ProtocolValidation.RequireDefined(capability, ProtocolValidationErrorCode.UnknownCapability, "payload.advertisedCapabilities", errors);
            }
        }

        // PublicKey (stage d part 2): optional — carried only; the ECDSA PEM check is
        // enforced at the connect boundary (crypto stays out of Contracts, which has no deps).
        return ProtocolValidationResult.From(errors);
    }
}

public sealed record AgentCommandEnvelope(Guid CommandId, Guid AgentId, AgentCapability Capability, string Argument, DateTimeOffset CreatedAt);
public sealed record AgentCommandResponse(Guid CommandId, Guid AgentId, CommandOutcome Outcome, string? Result, string? Error);

public sealed record CommandResult(
    Guid CommandId,
    CommandOutcome Outcome,
    string? FailureCode) : IProtocolPayload
{
    public ProtocolValidationResult Validate()
    {
        var errors = new List<ProtocolValidationError>();
        ProtocolValidation.RequireId(CommandId, ProtocolValidationErrorCode.MissingCommandId, "payload.commandId", errors);
        ProtocolValidation.RequireDefined(Outcome, ProtocolValidationErrorCode.UnknownCommandOutcome, "payload.outcome", errors);

        return ProtocolValidationResult.From(errors);
    }
}

public sealed record ProtocolEvent(
    Guid EventId,
    ProtocolEventKind Kind,
    DateTimeOffset OccurredAt,
    Guid TaskId) : IProtocolPayload
{
    public ProtocolValidationResult Validate()
    {
        var errors = new List<ProtocolValidationError>();
        ProtocolValidation.RequireId(EventId, ProtocolValidationErrorCode.MissingEventId, "payload.eventId", errors);
        ProtocolValidation.RequireDefined(Kind, ProtocolValidationErrorCode.UnknownEventKind, "payload.kind", errors);

        if (OccurredAt == default)
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MissingEventTimestamp, "payload.occurredAt"));
        }

        ProtocolValidation.RequireId(TaskId, ProtocolValidationErrorCode.MissingTaskId, "payload.taskId", errors);
        return ProtocolValidationResult.From(errors);
    }
}

public sealed record ProtocolEnvelope<TPayload>(
    ProtocolVersion ProtocolVersion,
    Guid CorrelationId,
    TPayload Payload)
    where TPayload : IProtocolPayload
{
    public ProtocolValidationResult Validate()
    {
        var errors = new List<ProtocolValidationError>();

        if (string.IsNullOrWhiteSpace(ProtocolVersion.Value))
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MissingProtocolVersion, "protocolVersion"));
        }
        else if (!ProtocolVersions.IsSupported(ProtocolVersion))
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.UnknownProtocolVersion, "protocolVersion"));
        }

        ProtocolValidation.RequireId(CorrelationId, ProtocolValidationErrorCode.MissingCorrelationId, "correlationId", errors);

        if (Payload is null)
        {
            errors.Add(new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "payload"));
        }
        else
        {
            errors.AddRange(Payload.Validate().Errors);
        }

        return ProtocolValidationResult.From(errors);
    }
}

public sealed record ProtocolParseResult<TPayload>(
    ProtocolEnvelope<TPayload>? Envelope,
    ProtocolValidationResult Validation)
    where TPayload : IProtocolPayload
{
    public bool IsValid => Envelope is not null && Validation.IsValid;
}

public static class ContractJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static string Serialize<TPayload>(ProtocolEnvelope<TPayload> envelope)
        where TPayload : IProtocolPayload => JsonSerializer.Serialize(envelope, SerializerOptions);

    public static ProtocolParseResult<TPayload> Parse<TPayload>(string json)
        where TPayload : IProtocolPayload
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ProtocolEnvelope<TPayload>>(json, SerializerOptions);
            var validation = envelope?.Validate() ?? ProtocolValidationResult.From([
                new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "envelope")]);

            return new ProtocolParseResult<TPayload>(envelope, validation);
        }
        catch (JsonException)
        {
            return new ProtocolParseResult<TPayload>(null, ProtocolValidationResult.From([
                new ProtocolValidationError(ProtocolValidationErrorCode.MalformedJson, "envelope")]));
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false
        };

        options.Converters.Add(new ProtocolVersionJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

internal static class ProtocolValidation
{
    public static void RequireId(
        Guid value,
        ProtocolValidationErrorCode code,
        string field,
        ICollection<ProtocolValidationError> errors)
    {
        if (value == Guid.Empty)
        {
            errors.Add(new ProtocolValidationError(code, field));
        }
    }

    public static void RequireDefined<TEnum>(
        TEnum value,
        ProtocolValidationErrorCode code,
        string field,
        ICollection<ProtocolValidationError> errors)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            errors.Add(new ProtocolValidationError(code, field));
        }
    }
}

internal sealed class ProtocolVersionJsonConverter : JsonConverter<ProtocolVersion>
{
    public override ProtocolVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Protocol version must be a string.");
        }

        return new ProtocolVersion(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, ProtocolVersion value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
