import { parseStrict, canonicalJson, StrictJsonError } from "./strict-json.js";
import { randomId } from "./clock.js";

export const PROTOCOL_VERSION = "v1";
export const SUPPORTED_PROTOCOL_VERSIONS = Object.freeze([PROTOCOL_VERSION]);

export const AgentCapability = Object.freeze({
  ReadFile: "ReadFile",
  ListDirectory: "ListDirectory",
  WriteFile: "WriteFile",
  RunBuild: "RunBuild",
  RunTests: "RunTests",
  GetProcessStatus: "GetProcessStatus",
  CancelTask: "CancelTask",
  RunCodex: "RunCodex",
  RunOpenCode: "RunOpenCode",
});

export const ToolRiskLevel = Object.freeze({
  ReadOnly: "ReadOnly",
  LowRisk: "LowRisk",
  Modify: "Modify",
  Destructive: "Destructive",
  Critical: "Critical",
});

export const RISK_ORDER = Object.freeze({ ReadOnly: 0, LowRisk: 1, Modify: 2, Destructive: 3, Critical: 4 });

export const IdentityRole = Object.freeze({
  Unknown: "Unknown",
  Guest: "Guest",
  TrustedGuest: "TrustedGuest",
  Family: "Family",
  Owner: "Owner",
});

export const MessageType = Object.freeze({
  ClientRequest: "ClientRequest",
  ClientResponse: "ClientResponse",
  HealthResponse: "HealthResponse",
  AgentRegistration: "AgentRegistration",
  CommandRequest: "CommandRequest",
  CommandResult: "CommandResult",
  ProtocolEvent: "ProtocolEvent",
});

export const ProtocolEventName = Object.freeze({
  TaskStarted: "TaskStarted",
  TaskProgress: "TaskProgress",
  ToolStarted: "ToolStarted",
  ToolCompleted: "ToolCompleted",
  TaskCompleted: "TaskCompleted",
  TaskFailed: "TaskFailed",
  AgentConnected: "AgentConnected",
  AgentDisconnected: "AgentDisconnected",
  ProactiveMessage: "ProactiveMessage",
  PermissionRequired: "PermissionRequired",
  ConfirmationRequired: "ConfirmationRequired",
  SecurityEvent: "SecurityEvent",
  LockedStateChange: "LockedStateChange",
  LeaseExpired: "LeaseExpired",
  DeviceRevoked: "DeviceRevoked",
});

export const ClientIntent = Object.freeze({
  Chat: "Chat",
  RunTask: "RunTask",
  ConfirmTask: "ConfirmTask",
  CancelTask: "CancelTask",
  HealthStatus: "HealthStatus",
  ListAgents: "ListAgents",
});

export const CommandStatus = Object.freeze({
  Succeeded: "Succeeded",
  Failed: "Failed",
  Denied: "Denied",
  TimedOut: "TimedOut",
  Cancelled: "Cancelled",
});

export const CAPABILITY_SPEC = Object.freeze({
  ReadFile: { risk: ToolRiskLevel.ReadOnly, resourceArgument: "path", scopedResource: true, destructive: false },
  ListDirectory: { risk: ToolRiskLevel.ReadOnly, resourceArgument: "path", scopedResource: true, destructive: false },
  WriteFile: { risk: ToolRiskLevel.Modify, resourceArgument: "path", scopedResource: true, destructive: false },
  RunBuild: { risk: ToolRiskLevel.LowRisk, resourceArgument: "projectPath", scopedResource: true, destructive: false },
  RunTests: { risk: ToolRiskLevel.LowRisk, resourceArgument: "projectPath", scopedResource: true, destructive: false },
  GetProcessStatus: { risk: ToolRiskLevel.ReadOnly, resourceArgument: null, scopedResource: false, destructive: false },
  CancelTask: { risk: ToolRiskLevel.LowRisk, resourceArgument: null, scopedResource: false, destructive: true },
  RunCodex: { risk: ToolRiskLevel.Modify, resourceArgument: "workingDirectory", scopedResource: true, destructive: false },
  RunOpenCode: { risk: ToolRiskLevel.Modify, resourceArgument: "workingDirectory", scopedResource: true, destructive: false },
});

export const ESCALATION_RULES = Object.freeze([
  {
    name: "security-configuration",
    capability: AgentCapability.WriteFile,
    argument: "path",
    re: /(\.git[\\/]hooks|nuget\.config|Directory\.Build\.(props|targets)|\.tolik[\\/]|policy\.json|CODEOWNERS)$/i,
    escalatedRisk: ToolRiskLevel.Destructive,
    requireConfirmation: true,
    reason: "modifying build, pipeline or authorization policy configuration",
  },
  {
    name: "security-critical-source",
    capability: AgentCapability.WriteFile,
    argument: "path",
    re: /(ProtocolContracts\.cs|CoreRuntime\.cs|AgentRuntime\.cs|AuthorizationEngine\.cs|SecurityLogging\.cs)$/i,
    escalatedRisk: ToolRiskLevel.Critical,
    requireConfirmation: true,
    reason: "modifying TOLIK's own security-critical source can bypass authorization (T13)",
  },
  {
    name: "codex-autonomy",
    capability: AgentCapability.RunCodex,
    argument: null,
    re: null,
    escalatedRisk: ToolRiskLevel.Modify,
    requireConfirmation: true,
    reason: "a coding agent can modify the working tree without review of each edit",
  },
]);

const FORBIDDEN_SURFACE_KEYS = new Set([
  "command",
  "commandline",
  "commandlineargument",
  "shell",
  "shellcommand",
  "cmd",
  "bash",
  "sh",
  "powershell",
  "pwsh",
  "script",
  "scripttext",
  "exec",
  "execute",
  "rawcommand",
  "rawscript",
  "argv",
  "eval",
]);

export class ProtocolValidationError extends Error {
  constructor(code, message, details = {}) {
    super(message);
    this.name = "ProtocolValidationError";
    this.code = code;
    this.details = details;
  }
}

const IDENTIFIER_RE = /^[A-Za-z0-9][A-Za-z0-9_.:@/-]{2,199}$/;
const ARGUMENT_NAME_RE = /^[a-z][a-zA-Z0-9_]{0,63}$/;

const S = {
  string({ nonEmpty = false, pattern = null, maxLength = 8192, patternName = null } = {}) {
    return { type: "string", nonEmpty, pattern, maxLength, patternName };
  },
  identifier() {
    return { type: "string", nonEmpty: true, pattern: IDENTIFIER_RE, maxLength: 200, patternName: "identifier" };
  },
  enumOf(name, values) {
    return { type: "enum", enumName: name, values: Object.values(values) };
  },
  number({ integer = true, min = null, max = null } = {}) {
    return { type: "number", integer, min, max };
  },
  boolean() {
    return { type: "boolean" };
  },
  arrayOf(item) {
    return { type: "array", item };
  },
  objectOf(value) {
    return { type: "object-map", value };
  },
  object(fields) {
    return { type: "object", fields, allowUnknown: false };
  },
};

function typeName(value) {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  return typeof value;
}

function validateNode(spec, value, path, errors) {
  const push = (code, message) => errors.push({ code, path, message });
  switch (spec.type) {
    case "string": {
      if (typeof value !== "string") return push("INVALID_FIELD_TYPE", `expected string, received ${typeName(value)}`);
      if (spec.nonEmpty && value.length === 0) return push("EMPTY_IDENTIFIER", "value must not be empty");
      if (value.length > spec.maxLength) return push("FIELD_TOO_LONG", `value exceeds ${spec.maxLength} characters`);
      if (spec.pattern && !spec.pattern.test(value)) {
        return push("INVALID_IDENTIFIER_FORMAT", `value does not match required ${spec.patternName ?? "pattern"}`);
      }
      return;
    }
    case "enum": {
      if (typeof value !== "string") {
        return push("INVALID_ENUM_TYPE", `enum ${spec.enumName} must be a string value, received ${typeName(value)}`);
      }
      if (!spec.values.includes(value)) {
        return push(`UNKNOWN_${spec.enumName.replace(/([a-z])([A-Z])/g, "$1_$2").toUpperCase()}`, `unknown ${spec.enumName} '${value}'`);
      }
      return;
    }
    case "number": {
      if (typeof value !== "number" || !Number.isFinite(value)) {
        return push("INVALID_FIELD_TYPE", `expected finite number, received ${typeName(value)}`);
      }
      if (spec.integer && !Number.isInteger(value)) return push("INVALID_FIELD_TYPE", "expected integer");
      if (spec.min !== null && value < spec.min) return push("FIELD_OUT_OF_RANGE", `value below minimum ${spec.min}`);
      if (spec.max !== null && value > spec.max) return push("FIELD_OUT_OF_RANGE", `value above maximum ${spec.max}`);
      return;
    }
    case "boolean": {
      if (typeof value !== "boolean") return push("INVALID_FIELD_TYPE", `expected boolean, received ${typeName(value)}`);
      return;
    }
    case "array": {
      if (!Array.isArray(value)) return push("INVALID_FIELD_TYPE", `expected array, received ${typeName(value)}`);
      if (value.length > 256) return push("FIELD_TOO_LONG", "array exceeds 256 entries");
      value.forEach((entry, index) => validateNode(spec.item, entry, `${path}[${index}]`, errors));
      return;
    }
    case "object-map": {
      if (!value || typeof value !== "object" || Array.isArray(value)) {
        return push("INVALID_FIELD_TYPE", `expected object, received ${typeName(value)}`);
      }
      const keys = Object.keys(value);
      if (keys.length > 64) return push("FIELD_TOO_LONG", "map exceeds 64 entries");
      for (const key of keys) {
        if (!ARGUMENT_NAME_RE.test(key)) push("INVALID_ARGUMENT_NAME", `argument name '${key}' is not permitted`);
        validateNode(spec.value, value[key], `${path}.${key}`, errors);
      }
      return;
    }
    case "object": {
      if (!value || typeof value !== "object" || Array.isArray(value)) {
        return push("INVALID_FIELD_TYPE", `expected object, received ${typeName(value)}`);
      }
      for (const key of Object.keys(value)) {
        if (!(key in spec.fields)) push("UNKNOWN_FIELD", `unknown field '${key}' at ${path || "<root>"}`);
      }
      for (const [key, fieldSpec] of Object.entries(spec.fields)) {
        const childPath = path ? `${path}.${key}` : key;
        if (!(key in value)) {
          errors.push({ code: "MISSING_FIELD", path: childPath, message: `missing required field '${childPath}'` });
          continue;
        }
        validateNode(fieldSpec, value[key], childPath, errors);
      }
      return;
    }
    default:
      push("INTERNAL_SCHEMA_ERROR", `unknown schema node type ${spec.type}`);
  }
}

function scanForbiddenSurface(value, path, errors) {
  if (!value || typeof value !== "object") return;
  for (const [key, child] of Object.entries(value)) {
    const normalized = key.toLowerCase().replace(/[-_\s]/g, "");
    const childPath = path ? `${path}.${key}` : key;
    if (FORBIDDEN_SURFACE_KEYS.has(normalized)) {
      errors.push({
        code: "FORBIDDEN_COMMAND_SURFACE",
        path: childPath,
        message: `field '${childPath}' would expose an unrestricted command surface`,
      });
    }
    scanForbiddenSurface(child, childPath, errors);
  }
}

const identitySchema = S.object({
  requesterId: S.identifier(),
  displayName: S.string({ nonEmpty: true, maxLength: 120 }),
  identityRole: S.enumOf("IdentityRole", IdentityRole),
  deviceId: S.identifier(),
  sessionId: S.identifier(),
});

const limitsSchema = S.object({
  timeoutMs: S.number({ min: 1, max: 3600000 }),
  maxOutputBytes: S.number({ min: 1, max: 1048576 }),
  maxIterations: S.number({ min: 1, max: 64 }),
});

const authorizationRefSchema = S.object({
  authorizationId: S.identifier(),
  decisionId: S.identifier(),
  leaseId: S.identifier(),
  issuedAtUtc: S.string({ nonEmpty: true, maxLength: 40 }),
  expiresAtUtc: S.string({ nonEmpty: true, maxLength: 40 }),
  constraints: limitsSchema,
});

const PAYLOAD_SCHEMAS = {
  [MessageType.ClientRequest]: S.object({
    requester: identitySchema,
    intent: S.enumOf("ClientIntent", ClientIntent),
    text: S.string({ maxLength: 20000 }),
    taskId: S.string({ maxLength: 200 }),
  }),
  [MessageType.ClientResponse]: S.object({
    responseKind: S.enumOf("ClientResponseKind", { Message: "Message", TaskUpdate: "TaskUpdate", Refused: "Refused" }),
    taskId: S.string({ maxLength: 200 }),
    text: S.string({ maxLength: 20000 }),
    decision: S.string({ maxLength: 64 }),
    detail: S.objectOf(S.string({ maxLength: 4000 })),
  }),
  [MessageType.HealthResponse]: S.object({
    service: S.string({ nonEmpty: true, maxLength: 64 }),
    status: S.enumOf("HealthStatus", { Healthy: "Healthy", Degraded: "Degraded", Unavailable: "Unavailable" }),
    role: S.string({ nonEmpty: true, maxLength: 64 }),
    declaredRoles: S.arrayOf(S.string({ nonEmpty: true, maxLength: 64 })),
    database: S.string({ nonEmpty: true, maxLength: 32 }),
    version: S.string({ nonEmpty: true, maxLength: 32 }),
    uptimeMs: S.number({ min: 0 }),
    locked: S.boolean(),
  }),
  [MessageType.AgentRegistration]: S.object({
    agentId: S.identifier(),
    agentName: S.string({ nonEmpty: true, maxLength: 120 }),
    platform: S.string({ nonEmpty: true, maxLength: 64 }),
    installId: S.identifier(),
    publicKeyJwk: S.object({
      kty: S.string({ nonEmpty: true, maxLength: 16 }),
      crv: S.string({ nonEmpty: true, maxLength: 32 }),
      x: S.string({ nonEmpty: true, maxLength: 256 }),
      y: S.string({ nonEmpty: true, maxLength: 256 }),
    }),
    declaredCapabilities: S.arrayOf(S.enumOf("AgentCapability", AgentCapability)),
    requestedRiskCeiling: S.enumOf("ToolRiskLevel", ToolRiskLevel),
  }),
  [MessageType.CommandRequest]: S.object({
    taskId: S.identifier(),
    stepId: S.identifier(),
    agentId: S.identifier(),
    capability: S.enumOf("AgentCapability", AgentCapability),
    declaredRisk: S.enumOf("ToolRiskLevel", ToolRiskLevel),
    arguments: S.objectOf(S.string({ maxLength: 200000 })),
    authorizationRef: authorizationRefSchema,
    limits: limitsSchema,
  }),
  [MessageType.CommandResult]: S.object({
    taskId: S.identifier(),
    stepId: S.identifier(),
    commandCorrelationId: S.identifier(),
    agentId: S.identifier(),
    capability: S.enumOf("AgentCapability", AgentCapability),
    status: S.enumOf("CommandStatus", CommandStatus),
    exitCode: S.number({ integer: true, min: -2147483648, max: 2147483647 }),
    stdout: S.string({ maxLength: 200000 }),
    stderr: S.string({ maxLength: 200000 }),
    startedAtUtc: S.string({ nonEmpty: true, maxLength: 40 }),
    completedAtUtc: S.string({ nonEmpty: true, maxLength: 40 }),
    durationMs: S.number({ min: 0 }),
    dataClassification: S.enumOf("DataClassification", { UntrustedToolOutput: "untrusted-tool-output" }),
    resultArtifact: S.string({ maxLength: 100000 }),
  }),
  [MessageType.ProtocolEvent]: S.object({
    eventName: S.enumOf("ProtocolEventName", ProtocolEventName),
    severity: S.enumOf("SecuritySeverity", { Info: "Info", Warning: "Warning", Security: "Security", Critical: "Critical" }),
    taskId: S.string({ maxLength: 200 }),
    agentId: S.string({ maxLength: 200 }),
    detail: S.string({ maxLength: 4000 }),
    data: S.objectOf(S.string({ maxLength: 4000 })),
  }),
};

export class ValidationResult {
  constructor(errors, value) {
    this.errors = errors;
    this.value = value;
  }
  get ok() {
    return this.errors.length === 0;
  }
  throwIfInvalid() {
    if (!this.ok) {
      const primary = this.errors[0];
      throw new ProtocolValidationError(primary.code, primary.message, { errors: this.errors });
    }
    return this.value;
  }
}

export function validateEnvelope(raw) {
  const errors = [];
  const allowed = ["protocolVersion", "correlationId", "messageType", "sentAtUtc", "payload"];
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    errors.push({ code: "MALFORMED_ENVELOPE", path: "", message: "envelope must be a JSON object" });
    return new ValidationResult(errors, null);
  }
  for (const key of Object.keys(raw)) {
    if (!allowed.includes(key)) errors.push({ code: "UNKNOWN_FIELD", path: key, message: `unknown envelope field '${key}'` });
  }
  if (typeof raw.protocolVersion !== "string") {
    errors.push({ code: "INVALID_FIELD_TYPE", path: "protocolVersion", message: "protocolVersion must be a string" });
  } else if (!SUPPORTED_PROTOCOL_VERSIONS.includes(raw.protocolVersion)) {
    errors.push({ code: "UNKNOWN_PROTOCOL_VERSION", path: "protocolVersion", message: `unsupported protocol version '${raw.protocolVersion}'` });
  }
  validateNode(S.identifier(), raw.correlationId, "correlationId", errors);
  if (typeof raw.messageType !== "string") {
    errors.push({ code: "INVALID_FIELD_TYPE", path: "messageType", message: "messageType must be a string" });
  } else if (!Object.values(MessageType).includes(raw.messageType)) {
    errors.push({ code: "UNKNOWN_MESSAGE_TYPE", path: "messageType", message: `unknown message type '${raw.messageType}'` });
  }
  validateNode(S.string({ nonEmpty: true, maxLength: 40 }), raw.sentAtUtc, "sentAtUtc", errors);
  const schema = PAYLOAD_SCHEMAS[raw.messageType];
  if (schema) {
    validateNode(schema, raw.payload, "payload", errors);
    scanForbiddenSurface(raw.payload, "payload", errors);
  }
  if (errors.length > 0) return new ValidationResult(errors, null);
  return new ValidationResult(errors, raw);
}

export function parseEnvelope(text) {
  let raw;
  try {
    raw = parseStrict(text);
  } catch (error) {
    if (error instanceof StrictJsonError) {
      const code = /duplicate property/.test(error.message) ? "DUPLICATE_PROPERTY" : "MALFORMED_JSON";
      return new ValidationResult([{ code, path: "", message: error.message }], null);
    }
    throw error;
  }
  return validateEnvelope(raw);
}

export function buildEnvelope(messageType, payload, { correlationId, sentAtUtc } = {}) {
  return {
    protocolVersion: PROTOCOL_VERSION,
    correlationId: correlationId ?? randomId("cor"),
    messageType,
    sentAtUtc: sentAtUtc ?? new Date().toISOString(),
    payload,
  };
}

export function serializeEnvelope(envelope) {
  return canonicalJson(envelope);
}

export function payloadSchemaFor(messageType) {
  return PAYLOAD_SCHEMAS[messageType] ?? null;
}

const PLAN_STEP_SCHEMA = S.object({
  capability: S.enumOf("AgentCapability", AgentCapability),
  arguments: S.objectOf(S.string({ maxLength: 200000 })),
  declaredRisk: S.enumOf("ToolRiskLevel", ToolRiskLevel),
  rationale: S.string({ maxLength: 2000 }),
});

export function validatePlanStep(raw, path = "step") {
  const errors = [];
  validateNode(PLAN_STEP_SCHEMA, raw, path, errors);
  scanForbiddenSurface(raw, path, errors);
  return new ValidationResult(errors, errors.length === 0 ? raw : null);
}

export function validatePlan(raw) {
  const errors = [];
  if (!Array.isArray(raw)) {
    return new ValidationResult([{ code: "MALFORMED_PLAN", path: "plan", message: "plan must be an array of steps" }], null);
  }
  if (raw.length > 24) {
    return new ValidationResult([{ code: "PLAN_TOO_LONG", path: "plan", message: "plan exceeds 24 steps" }], null);
  }
  const steps = [];
  raw.forEach((entry, index) => {
    const result = validatePlanStep(entry, `plan[${index}]`);
    if (result.ok) steps.push(result.value);
    else errors.push(...result.errors);
  });
  return new ValidationResult(errors, errors.length === 0 ? steps : null);
}

export function describeContracts() {
  return {
    protocolVersion: PROTOCOL_VERSION,
    messageTypes: Object.values(MessageType),
    capabilities: Object.entries(CAPABILITY_SPEC).map(([name, spec]) => ({
      capability: name,
      minimumRisk: spec.risk,
      resourceArgument: spec.resourceArgument,
      scopedResource: spec.scopedResource,
    })),
    riskLevels: Object.values(ToolRiskLevel),
    identityRoles: Object.values(IdentityRole),
    escalationRules: ESCALATION_RULES.map((rule) => ({
      name: rule.name,
      capability: rule.capability,
      escalatedRisk: rule.escalatedRisk,
      requireConfirmation: Boolean(rule.requireConfirmation),
      reason: rule.reason,
    })),
    forbiddenCommandSurface: [...FORBIDDEN_SURFACE_KEYS],
  };
}
