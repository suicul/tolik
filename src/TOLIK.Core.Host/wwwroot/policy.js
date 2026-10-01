import {
  AgentCapability,
  CAPABILITY_SPEC,
  ESCALATION_RULES,
  IdentityRole,
  RISK_ORDER,
  ToolRiskLevel,
} from "./contracts.js";
import { isSensitivePath, sensitivePathRuleName } from "./secrets.js";

export const Decision = Object.freeze({
  Allow: "ALLOW",
  Deny: "DENY",
  RequireConfirmation: "REQUIRE_CONFIRMATION",
});

export const ROLE_ORDER = Object.freeze({
  [IdentityRole.Unknown]: 0,
  [IdentityRole.Guest]: 1,
  [IdentityRole.TrustedGuest]: 2,
  [IdentityRole.Family]: 3,
  [IdentityRole.Owner]: 4,
});

export const ROLE_RISK_CEILING = Object.freeze({
  [IdentityRole.Unknown]: null,
  [IdentityRole.Guest]: ToolRiskLevel.ReadOnly,
  [IdentityRole.TrustedGuest]: ToolRiskLevel.LowRisk,
  [IdentityRole.Family]: ToolRiskLevel.Modify,
  [IdentityRole.Owner]: ToolRiskLevel.Critical,
});

export const CAPABILITY_MIN_ROLE = Object.freeze({
  [AgentCapability.ReadFile]: IdentityRole.Guest,
  [AgentCapability.ListDirectory]: IdentityRole.Guest,
  [AgentCapability.GetProcessStatus]: IdentityRole.Guest,
  [AgentCapability.RunBuild]: IdentityRole.TrustedGuest,
  [AgentCapability.RunTests]: IdentityRole.TrustedGuest,
  [AgentCapability.CancelTask]: IdentityRole.TrustedGuest,
  [AgentCapability.WriteFile]: IdentityRole.Family,
  [AgentCapability.RunCodex]: IdentityRole.Owner,
  [AgentCapability.RunOpenCode]: IdentityRole.Owner,
});

export const CONFIRMATION_METHODS = Object.freeze({
  ExplicitUserConfirm: "explicit-user-confirm",
  OwnerStrongAuth: "strong-auth:owner-mfa",
});

const FORBIDDEN_ARGUMENT_KEYS = new Set([
  "command",
  "commandline",
  "shell",
  "shellcommand",
  "cmd",
  "bash",
  "powershell",
  "pwsh",
  "script",
  "scripttext",
  "exec",
  "execute",
  "rawcommand",
  "argv",
  "eval",
]);

export function normalizePath(path) {
  return String(path ?? "")
    .replace(/\\/g, "/")
    .replace(/\/+/g, "/")
    .replace(/\/\.\//g, "/")
    .toLowerCase();
}

export function resolvePathSegments(path) {
  const normalized = String(path ?? "").replace(/\\/g, "/");
  const isAbsolute = normalized.startsWith("/") || /^[a-zA-Z]:\//.test(normalized);
  const segments = [];
  for (const segment of normalized.split("/")) {
    if (segment === "" || segment === ".") continue;
    if (segment === "..") {
      if (segments.length === 0) return { escaped: true, segments: [] };
      segments.pop();
      continue;
    }
    segments.push(segment);
  }
  return { escaped: false, segments, isAbsolute };
}

export function pathWithinScope(path, scopes) {
  if (!Array.isArray(scopes) || scopes.length === 0) return false;
  const target = resolvePathSegments(path);
  if (target.escaped) return false;
  const targetKey = target.segments.map((s) => s.toLowerCase()).join("/");
  return scopes.some((scope) => {
    if (scope === "*") return false;
    const scopeResolved = resolvePathSegments(scope);
    if (scopeResolved.escaped) return false;
    const scopeKey = scopeResolved.segments.map((s) => s.toLowerCase()).join("/");
    if (scopeKey === "") return false;
    return targetKey === scopeKey || targetKey.startsWith(`${scopeKey}/`);
  });
}

export function computeEffectiveRisk(capability, declaredRisk, args = {}) {
  const spec = CAPABILITY_SPEC[capability];
  const applied = [];
  let risk = spec ? spec.risk : null;
  if (spec && declaredRisk && RISK_ORDER[declaredRisk] < RISK_ORDER[spec.risk]) {
    applied.push({ rule: "declared-risk-below-minimum", declaredRisk, clampedTo: spec.risk });
  }
  if (declaredRisk && RISK_ORDER[declaredRisk] > RISK_ORDER[risk]) risk = declaredRisk;
  let requireConfirmation = false;
  for (const rule of ESCALATION_RULES) {
    if (rule.capability !== capability) continue;
    const matches = rule.argument === null ? true : rule.re.test(String(args[rule.argument] ?? ""));
    if (!matches) continue;
    applied.push({ rule: rule.name, escalatedTo: rule.escalatedRisk, reason: rule.reason });
    if (RISK_ORDER[rule.escalatedRisk] > RISK_ORDER[risk]) risk = rule.escalatedRisk;
    if (rule.requireConfirmation) requireConfirmation = true;
  }
  if (RISK_ORDER[risk] >= RISK_ORDER[ToolRiskLevel.Destructive]) requireConfirmation = true;
  return { risk, minimumRisk: spec ? spec.risk : null, requireConfirmation, appliedRules: applied };
}

export class AuthorizationEngine {
  constructor({ policyAvailable = true, integrityOk = true } = {}) {
    this.policyAvailable = policyAvailable;
    this.integrityOk = integrityOk;
  }

  evaluate(request, context) {
    const checks = [];
    const fail = (code, reason, extra = {}) => {
      checks.push({ name: code, result: "fail", detail: reason });
      return {
        decision: Decision.Deny,
        code,
        reason,
        checks,
        ...extra,
      };
    };
    const pass = (name, detail) => checks.push({ name, result: "pass", detail });

    const spec = CAPABILITY_SPEC[request.capability];
    const role = context.identity?.role ?? IdentityRole.Unknown;
    const effective = spec
      ? computeEffectiveRisk(request.capability, request.declaredRisk, request.arguments ?? {})
      : { risk: null, minimumRisk: null, requireConfirmation: false, appliedRules: [] };

    if (context.coreState === "LOCKED") return fail("LOCKED_STATE", "Core is in LOCKED state; sensitive operations are denied.");
    pass("core-state", "Core is not locked.");

    if (!this.policyAvailable || context.policyAvailable === false) {
      return fail("POLICY_UNAVAILABLE", "Authorization policy is unavailable; failing closed.");
    }
    pass("policy-available", "Authorization policy is loaded.");

    if (!this.integrityOk || context.integrityOk === false) {
      return fail("INTEGRITY_FAILURE", "Integrity verification failed; failing closed.");
    }
    pass("integrity", "Binary and configuration integrity verified.");

    if (context.revocationFresh === false && spec && RISK_ORDER[spec.risk] >= RISK_ORDER[ToolRiskLevel.Modify]) {
      return fail(
        "REVOCATION_STATE_STALE",
        "Revocation state cannot be verified against a remote authority and the operation modifies state; failing closed.",
      );
    }
    pass("revocation-freshness", "Revocation state is fresh or the operation is read-only.");

    if (!context.identity || context.identity.authenticated !== true) {
      return fail("NO_IDENTITY", "No authenticated identity is present for this request.");
    }
    pass("identity", `Authenticated as ${context.identity.requesterId} (${role}).`);

    if (ROLE_ORDER[role] === undefined || role === IdentityRole.Unknown) {
      return fail("UNKNOWN_ROLE", `Identity role '${role}' cannot authorize execution.`);
    }
    pass("role-known", `Role '${role}' recognized.`);

    if (context.device?.revoked) {
      return fail("DEVICE_REVOKED", "The requesting device is revoked.");
    }
    pass("device", `Device '${context.device?.deviceId ?? "unknown"}' is not revoked.`);

    if (!context.session || context.session.active !== true) {
      return fail("SESSION_INVALID", `Session is not active (${context.session?.reason ?? "SESSION_MISSING"}).`);
    }
    pass("session", "Session is active and unexpired.");

    if (!spec) return fail("UNKNOWN_CAPABILITY", `Capability '${request.capability}' is not in the allowlist.`);
    pass("capability-allowlisted", `${request.capability} is an allowlisted capability.`);

    const forbidden = Object.keys(request.arguments ?? {}).filter((key) =>
      FORBIDDEN_ARGUMENT_KEYS.has(key.toLowerCase().replace(/[-_\s]/g, "")),
    );
    if (forbidden.length > 0) {
      return fail("FORBIDDEN_COMMAND_SURFACE", `Arguments contain a forbidden command surface: ${forbidden.join(", ")}.`);
    }
    pass("no-command-surface", "Arguments contain no unrestricted command surface.");

    if (effective.appliedRules.some((rule) => rule.rule === "declared-risk-below-minimum")) {
      checks.push({
        name: "declared-risk-clamped",
        result: "pass",
        detail: `Declared risk '${request.declaredRisk}' is below the capability minimum; Core computed '${effective.risk}' instead. A declared risk never grants permission.`,
      });
    }

    const minRole = CAPABILITY_MIN_ROLE[request.capability];
    if (ROLE_ORDER[role] < ROLE_ORDER[minRole]) {
      return fail(
        "CAPABILITY_NOT_PERMITTED_FOR_ROLE",
        `Role '${role}' may not invoke ${request.capability} (requires at least '${minRole}').`,
        { effectiveRisk: effective.risk },
      );
    }
    pass("role-permitted", `Role '${role}' may invoke ${request.capability}.`);

    const ceiling = ROLE_RISK_CEILING[role];
    if (ceiling === null || RISK_ORDER[effective.risk] > RISK_ORDER[ceiling]) {
      return fail(
        "RISK_CEILING_EXCEEDED",
        `Effective risk '${effective.risk}' exceeds the ceiling '${ceiling ?? "none"}' for role '${role}'.`,
        { effectiveRisk: effective.risk },
      );
    }
    pass("risk-ceiling", `Effective risk '${effective.risk}' is within the '${ceiling}' ceiling for '${role}'.`);

    let resourceValue = null;
    if (spec.scopedResource && spec.resourceArgument) {
      resourceValue = request.arguments?.[spec.resourceArgument];
      if (typeof resourceValue !== "string" || resourceValue.length === 0) {
        return fail("RESOURCE_ARGUMENT_MISSING", `Capability ${request.capability} requires argument '${spec.resourceArgument}'.`);
      }
      pass("resource-present", `Resource '${resourceValue}' supplied.`);

      if (isSensitivePath(resourceValue)) {
        return fail(
          "SENSITIVE_RESOURCE",
          `Resource matches sensitive pattern '${sensitivePathRuleName(resourceValue)}'; denied by default.`,
          { effectiveRisk: effective.risk },
        );
      }
      pass("resource-not-sensitive", "Resource does not match a sensitive-data pattern.");

      const resolved = resolvePathSegments(resourceValue);
      if (resolved.escaped) {
        return fail("PATH_TRAVERSAL", "Resource path escapes its root through traversal segments.");
      }
      pass("path-canonical", "Resource path contains no escaping traversal.");

      if (!pathWithinScope(resourceValue, context.lease?.resourceScopes)) {
        return fail(
          "RESOURCE_OUT_OF_SCOPE",
          `Resource is outside the lease scope [${(context.lease?.resourceScopes ?? []).join(", ")}].`,
          { effectiveRisk: effective.risk },
        );
      }
      pass("resource-in-scope", "Resource is inside the lease scope.");
    } else {
      checks.push({ name: "resource-scope", result: "skip", detail: "Capability targets no filesystem resource." });
    }

    if (!context.lease || context.lease.present !== true) {
      return fail("LEASE_MISSING", "No authorization lease is bound to this request.", { effectiveRisk: effective.risk });
    }
    if (context.lease.valid !== true) {
      return fail("LEASE_INVALID", `Authorization lease is not valid (${context.lease.reason ?? "LEASE_UNKNOWN"}).`, {
        effectiveRisk: effective.risk,
      });
    }
    pass("lease-valid", `Lease valid until ${context.lease.expiresAtUtc}.`);

    if (!context.lease.capabilities?.includes(request.capability)) {
      return fail("LEASE_CAPABILITY_MISSING", `Lease does not include capability ${request.capability}.`, {
        effectiveRisk: effective.risk,
      });
    }
    pass("lease-capability", "Lease includes the requested capability.");

    if (context.lease.riskCeiling && RISK_ORDER[effective.risk] > RISK_ORDER[context.lease.riskCeiling]) {
      return fail("LEASE_RISK_CEILING_EXCEEDED", `Effective risk '${effective.risk}' exceeds lease ceiling.`, {
        effectiveRisk: effective.risk,
      });
    }
    pass("lease-risk", "Effective risk is within the lease ceiling.");

    if (context.authorizationAlreadyConsumed) {
      return fail("AUTHORIZATION_ALREADY_CONSUMED", "This authorization has already been consumed; replay rejected.", {
        effectiveRisk: effective.risk,
      });
    }
    pass("authorization-fresh", "Authorization has not been consumed before.");

    if (effective.requireConfirmation) {
      const confirmation = context.confirmation;
      if (!confirmation || confirmation.approved !== true) {
        checks.push({
          name: "confirmation",
          result: "fail",
          detail: `Risk '${effective.risk}' requires explicit confirmation which has not been supplied.`,
        });
        return {
          decision: Decision.RequireConfirmation,
          code: "CONFIRMATION_REQUIRED",
          reason: `Operation risk '${effective.risk}' requires explicit confirmation.`,
          effectiveRisk: effective.risk,
          minimumRisk: effective.minimumRisk,
          appliedRules: effective.appliedRules,
          checks,
          resource: resourceValue,
        };
      }
      if (confirmation.boundTo !== request.bindingHash) {
        return fail("CONFIRMATION_NOT_BOUND", "Confirmation is not bound to this exact request; failing closed.", {
          effectiveRisk: effective.risk,
        });
      }
      if (!Object.values(CONFIRMATION_METHODS).includes(confirmation.method)) {
        return fail("CONFIRMATION_METHOD_INVALID", `Confirmation method '${confirmation.method}' is not recognized.`, {
          effectiveRisk: effective.risk,
        });
      }
      if (confirmation.expiresAtUtc && Date.parse(confirmation.expiresAtUtc) <= context.nowMs) {
        return fail("CONFIRMATION_EXPIRED", "Confirmation has expired.", { effectiveRisk: effective.risk });
      }
      if (effective.risk === ToolRiskLevel.Critical && confirmation.method !== CONFIRMATION_METHODS.OwnerStrongAuth) {
        return fail("CONFIRMATION_TOO_WEAK", "Critical operations require owner strong authentication, not a plain confirmation.", {
          effectiveRisk: effective.risk,
        });
      }
      pass("confirmation", `Confirmation '${confirmation.confirmationId}' honored (${confirmation.method}).`);
    } else {
      checks.push({ name: "confirmation", result: "skip", detail: "Risk level does not require confirmation." });
    }

    return {
      decision: Decision.Allow,
      code: "ALLOWED",
      reason: `Authorized ${request.capability} at risk '${effective.risk}'.`,
      effectiveRisk: effective.risk,
      minimumRisk: effective.minimumRisk,
      appliedRules: effective.appliedRules,
      checks,
      resource: resourceValue,
      constraints: {
        timeoutMs: context.lease?.constraints?.timeoutMs ?? 30000,
        maxOutputBytes: context.lease?.constraints?.maxOutputBytes ?? 65536,
        maxIterations: context.lease?.maxIterations ?? 8,
      },
    };
  }
}
