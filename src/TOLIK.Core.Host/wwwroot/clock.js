export class SystemClock {
  now() {
    return new Date();
  }
  nowMs() {
    return Date.now();
  }
}

export class TestClock {
  constructor(startIso = "2026-01-01T00:00:00Z") {
    this._ms = Date.parse(startIso);
  }
  now() {
    return new Date(this._ms);
  }
  nowMs() {
    return this._ms;
  }
  advance(ms) {
    this._ms += ms;
    return this;
  }
  set(iso) {
    this._ms = Date.parse(iso);
    return this;
  }
}

export function isoNow(clock) {
  return clock.now().toISOString();
}

export function randomId(prefix = "id", clock) {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  return `${prefix}_${hex}`;
}

export function randomToken(byteLength = 32) {
  const bytes = new Uint8Array(byteLength);
  crypto.getRandomValues(bytes);
  return base64url(bytes);
}

export function base64url(bytes) {
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function fromBase64url(text) {
  const padded = text.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((text.length + 3) % 4);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

export function utf8(text) {
  return new TextEncoder().encode(text);
}

export async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", typeof text === "string" ? utf8(text) : text);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export async function sha256B64url(text) {
  const digest = await crypto.subtle.digest("SHA-256", typeof text === "string" ? utf8(text) : text);
  return base64url(new Uint8Array(digest));
}
