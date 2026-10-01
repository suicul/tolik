const SECRET_KEY_RE =
  /(api[-_]?key|apikey|secret|passwd|password|pwd|token|bearer|authorization|auth[-_]?header|cookie|set[-_]?cookie|connection[-_]?string|private[-_]?key|client[-_]?secret|access[-_]?key|refresh[-_]?token|credential|session[-_]?key|signing[-_]?key)/i;

const TEXT_RULES = [
  {
    name: "private-key-block",
    re: /-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----/g,
    replace: () => "-----BEGIN PRIVATE KEY-----[REDACTED]-----END PRIVATE KEY-----",
  },
  { name: "json-web-token", re: /\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\b/g, replace: () => "[REDACTED:JWT]" },
  { name: "bearer-token", re: /\bBearer\s+[A-Za-z0-9._~+/=-]{8,}/gi, replace: () => "Bearer [REDACTED]" },
  { name: "openai-key", re: /\bsk-[A-Za-z0-9_-]{12,}\b/g, replace: () => "[REDACTED:API_KEY]" },
  { name: "aws-access-key", re: /\b(AKIA|ASIA)[0-9A-Z]{16}\b/g, replace: () => "[REDACTED:AWS_KEY]" },
  { name: "github-token", re: /\bgh[pousr]_[A-Za-z0-9]{16,}\b/g, replace: () => "[REDACTED:GITHUB_TOKEN]" },
  { name: "slack-token", re: /\bxox[baprs]-[A-Za-z0-9-]{10,}\b/g, replace: () => "[REDACTED:SLACK_TOKEN]" },
  {
    name: "url-credential",
    re: /\b([a-z][a-z0-9+.-]*:\/\/)[^/\s@:]+:[^/\s@]+@/gi,
    replace: (match, scheme) => `${scheme}[REDACTED]:[REDACTED]@`,
  },
  {
    name: "secret-query-param",
    re: /([?&](?:api[-_]?key|key|token|access[-_]?token|password|secret|signature|sig|code)=)[^&\s"'<>]+/gi,
    replace: (match, prefix) => `${prefix}[REDACTED]`,
  },
  {
    name: "secret-assignment",
    re: /\b(api[-_]?key|apikey|password|passwd|secret|token|private[-_]?key|connection[-_]?string)\b\s*[:=]\s*("[^"]*"|'[^']*'|[^\s,;)"']+)/gi,
    replace: (match, name) => `${name}=[REDACTED]`,
  },
  {
    name: "ssh-private-key-path-with-material",
    re: /\b(ssh-rsa|ssh-ed25519|ecdsa-sha2-nistp[0-9]+)\s+[A-Za-z0-9+/=]{32,}/g,
    replace: (match, kind) => `${kind} [REDACTED]`,
  },
];

export const SENSITIVE_PATH_RULES = [
  { name: "dotenv", re: /(^|[\\/])\.env(\.[a-z0-9]+)?$/i },
  { name: "env-variant", re: /(^|[\\/])\.env(\.|$)/i },
  { name: "ssh-material", re: /(^|[\\/])\.ssh([\\/]|$)/i },
  { name: "private-key-file", re: /(^|[\\/])(id_rsa|id_ed25519|id_ecdsa|id_dsa)(\.pub)?$/i },
  { name: "private-key-extension", re: /\.(pem|pfx|p12|key|jks|keystore)$/i },
  { name: "cloud-credentials", re: /(^|[\\/])\.aws([\\/]|$)|(^|[\\/])\.config[\\/]gcloud([\\/]|$)|(^|[\\/])\.azure([\\/]|$)/i },
  { name: "git-credentials", re: /(^|[\\/])\.git-credentials$|(^|[\\/])\.netrc$|(^|[\\/])_netrc$/i },
  { name: "credential-store", re: /(credential|password|passwords|secrets?|vault|keychain)[\\/]/i },
  { name: "browser-profile", re: /(^|[\\/])(Cookies|Login Data|Web Data|key[34]\.db|logins\.json|Local State)$/i },
  { name: "browser-profile-root", re: /(^|[\\/])(User Data|Chrome|Firefox|Edge|BraveSoftware)([\\/]|$)/i },
  { name: "tolik-secrets", re: /(^|[\\/])(security\.json|appsettings(\.[A-Za-z]+)?\.json|secrets(\.[a-z]+)?\.json)$/i },
  { name: "system-secret-store", re: /(^|[\\/])(SAM|SYSTEM|SECURITY)$|(^|[\\/])etc[\\/]shadow$/ },
];

export const SECRET_CLASSIFICATION = Object.freeze({
  credential: ["api key", "access token", "password", "private key", "ssh key", "cookie", "credential store", "session material", "connection string", "provider credential"],
  sensitiveData: ["personal memory", "private project", "household data", "screen content", "camera content", "microphone content", "raw tool output"],
});

function freshRuleRe(rule) {
  return new RegExp(rule.re.source, rule.re.flags);
}

export function redactText(text) {
  if (typeof text !== "string") return { text, redactions: 0, kinds: [] };
  let out = text;
  const kinds = new Set();
  let redactions = 0;
  for (const rule of TEXT_RULES) {
    const re = freshRuleRe(rule);
    out = out.replace(re, (...args) => {
      redactions += 1;
      kinds.add(rule.name);
      return rule.replace(...args);
    });
  }
  return { text: out, redactions, kinds: [...kinds] };
}

export function isSecretKeyName(key) {
  return typeof key === "string" && SECRET_KEY_RE.test(key);
}

export function redactValue(value, keyName = "") {
  if (isSecretKeyName(keyName)) {
    if (value === null || value === undefined) return value;
    return typeof value === "string" ? "[REDACTED]" : "[REDACTED_VALUE]";
  }
  if (typeof value === "string") return redactText(value).text;
  if (Array.isArray(value)) return value.map((entry) => redactValue(entry));
  if (value && typeof value === "object") {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = redactValue(v, k);
    return out;
  }
  return value;
}

export function redactRecord(record) {
  if (!record || typeof record !== "object") return record;
  const out = {};
  for (const [k, v] of Object.entries(record)) out[k] = redactValue(v, k);
  return out;
}

export function isSensitivePath(path) {
  if (typeof path !== "string" || path.length === 0) return false;
  const normalized = path.replace(/\\/g, "/");
  return SENSITIVE_PATH_RULES.some((rule) => rule.re.test(normalized) || rule.re.test(path));
}

export function sensitivePathRuleName(path) {
  const normalized = String(path ?? "").replace(/\\/g, "/");
  for (const rule of SENSITIVE_PATH_RULES) {
    if (rule.re.test(normalized) || rule.re.test(String(path ?? ""))) return rule.name;
  }
  return null;
}

export function scanForSecrets(text, { maxPreview = 0 } = {}) {
  if (typeof text !== "string") return [];
  const findings = [];
  for (const rule of TEXT_RULES) {
    const re = freshRuleRe(rule);
    let match;
    while ((match = re.exec(text)) !== null) {
      const raw = match[0];
      findings.push({
        kind: rule.name,
        offset: match.index,
        length: raw.length,
        preview: maxPreview > 0 ? `${raw.slice(0, maxPreview)}...` : undefined,
      });
      if (match.index === re.lastIndex) re.lastIndex += 1;
    }
  }
  return findings;
}
