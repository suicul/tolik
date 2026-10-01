import { randomId, randomToken, base64url, fromBase64url, utf8, sha256B64url } from "./clock.js";

const DB_NAME = "tolik-identity";
const DB_VERSION = 1;
const STORES = ["install", "devices", "sessions", "leases", "revocations", "enrollmentTokens", "settings"];

class IndexedDbBacking {
  constructor(db) {
    this.db = db;
  }

  static async open() {
    const db = await new Promise((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, DB_VERSION);
      request.onupgradeneeded = () => {
        const database = request.result;
        for (const store of STORES) {
          if (!database.objectStoreNames.contains(store)) database.createObjectStore(store, { keyPath: "id" });
        }
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error ?? new Error("indexeddb open failed"));
    });
    return new IndexedDbBacking(db);
  }

  _run(store, mode, operation) {
    return new Promise((resolve, reject) => {
      const tx = this.db.transaction(store, mode);
      const request = operation(tx.objectStore(store));
      tx.onerror = () => reject(tx.error ?? new Error("indexeddb transaction failed"));
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error ?? new Error("indexeddb request failed"));
    });
  }

  get(store, id) {
    return this._run(store, "readonly", (objectStore) => objectStore.get(id));
  }

  put(store, record) {
    return this._run(store, "readwrite", (objectStore) => objectStore.put(record));
  }

  delete(store, id) {
    return this._run(store, "readwrite", (objectStore) => objectStore.delete(id));
  }

  all(store) {
    return this._run(store, "readonly", (objectStore) => objectStore.getAll());
  }
}

class MemoryBacking {
  constructor() {
    this._data = new Map(STORES.map((store) => [store, new Map()]));
  }

  async get(store, id) {
    return this._data.get(store).get(id) ?? null;
  }

  async put(store, record) {
    this._data.get(store).set(record.id, record);
    return record.id;
  }

  async delete(store, id) {
    this._data.get(store).delete(id);
  }

  async all(store) {
    return [...this._data.get(store).values()];
  }
}

export class IdentityStore {
  constructor({ clock, audit, persistent = true } = {}) {
    this.clock = clock;
    this.audit = audit;
    this.persistent = persistent;
    this.backing = null;
    this.install = null;
    this.secretsAvailable = true;
    this._pinnedAgentKeys = new Map();
  }

  async init() {
    this.backing = this.persistent ? await IndexedDbBacking.open() : new MemoryBacking();
    this.install = await this.backing.get("install", "install");
    if (!this.install) {
      this.install = await this._createInstallIdentity();
      await this.backing.put("install", this.install);
      this._audit("Info", "Identity", "InstallIdentityCreated", "A new installation identity was created and bound to a non-extractable key.", {
        installId: this.install.installId,
        keyId: this.install.kid,
        algorithm: this.install.algorithm,
      });
    }
    return this;
  }

  async _createInstallIdentity() {
    const keyPair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, false, ["sign", "verify"]);
    const publicJwk = await crypto.subtle.exportKey("jwk", keyPair.publicKey);
    const installId = randomId("install");
    return {
      id: "install",
      installId,
      kid: randomId("kid"),
      algorithm: "ECDSA-P256-SHA256",
      publicJwk,
      privateKey: keyPair.privateKey,
      privateKeyExtractable: keyPair.privateKey.extractable,
      keyStorage: "non-extractable CryptoKey held in IndexedDB",
      createdAtUtc: this.clock.now().toISOString(),
    };
  }

  _audit(level, category, eventName, message, properties = {}, scopes = {}) {
    this.audit?.record({ level, category, eventName, message, properties, scopes });
  }

  async sign(bytes) {
    if (!this.secretsAvailable) throw new Error("secrets-unavailable");
    const signature = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, this.install.privateKey, bytes);
    return { kid: this.install.kid, algorithm: this.install.algorithm, signature: base64url(new Uint8Array(signature)) };
  }

  async verifyWithPublicJwk(publicJwk, bytes, signatureB64) {
    try {
      const key = await crypto.subtle.importKey("jwk", publicJwk, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
      return await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, fromBase64url(signatureB64), bytes);
    } catch {
      return false;
    }
  }

  async verifyInstallSignature(bytes, signatureB64) {
    return this.verifyWithPublicJwk(this.install.publicJwk, bytes, signatureB64);
  }

  async attemptPrivateKeyExport() {
    try {
      await crypto.subtle.exportKey("pkcs8", this.install.privateKey);
      return { exported: true, error: null };
    } catch (error) {
      return { exported: false, error: error?.name ?? "UnknownError" };
    }
  }

  pinAgentKey(agentId, publicJwk) {
    this._pinnedAgentKeys.set(agentId, publicJwk);
  }

  pinnedAgentKey(agentId) {
    return this._pinnedAgentKeys.get(agentId) ?? null;
  }

  async registerDevice({ name, platform, role, installId, publicKeyJwk = null }) {
    const device = {
      id: randomId("dev"),
      displayId: randomId("device"),
      name,
      platform,
      role,
      installId: installId ?? this.install.installId,
      publicKeyJwk: publicKeyJwk ?? this.install.publicJwk,
      registeredAtUtc: this.clock.now().toISOString(),
      lastSeenAtUtc: this.clock.now().toISOString(),
      revoked: false,
      revokedAtUtc: null,
      revokedReason: null,
    };
    await this.backing.put("devices", device);
    this._audit("Info", "Identity", "DeviceRegistered", `Device '${name}' registered as ${role}.`, {
      deviceId: device.id,
      platform,
      role,
    });
    return device;
  }

  async listDevices() {
    return this.backing.all("devices");
  }

  async getDevice(deviceId) {
    return this.backing.get("devices", deviceId);
  }

  async revokeDevice(deviceId, reason) {
    const device = await this.backing.get("devices", deviceId);
    if (!device) return { ok: false, reason: "DEVICE_NOT_FOUND" };
    device.revoked = true;
    device.revokedAtUtc = this.clock.now().toISOString();
    device.revokedReason = reason;
    await this.backing.put("devices", device);
    for (const session of await this.listSessions()) {
      if (session.deviceId === deviceId && !session.revoked) {
        await this.revokeSession(session.id, `device-revoked:${reason}`);
      }
    }
    const revocation = {
      id: randomId("rev"),
      subjectType: "device",
      subjectId: deviceId,
      reason,
      issuedAtUtc: this.clock.now().toISOString(),
      appliedAtUtc: this.clock.now().toISOString(),
    };
    await this.backing.put("revocations", revocation);
    this._audit("Security", "Identity", "DeviceRevoked", `Device '${device.name}' revoked: ${reason}.`, {
      deviceId,
      reason,
    });
    return { ok: true, device, revocation };
  }

  async createSession({ deviceId, personName, userId, role, ttlMs = 15 * 60 * 1000 }) {
    const device = await this.backing.get("devices", deviceId);
    if (!device) return { ok: false, reason: "DEVICE_NOT_FOUND" };
    if (device.revoked) {
      this._audit("Security", "Identity", "SessionRejected", "Session creation rejected for a revoked device.", {
        deviceId,
        reason: "DEVICE_REVOKED",
      });
      return { ok: false, reason: "DEVICE_REVOKED" };
    }
    const token = randomToken(32);
    const now = this.clock.now().getTime();
    const session = {
      id: randomId("sess"),
      tokenHash: await sha256B64url(token),
      userId,
      personName,
      deviceId,
      role,
      createdAtUtc: new Date(now).toISOString(),
      expiresAtUtc: new Date(now + ttlMs).toISOString(),
      revoked: false,
      revokedAtUtc: null,
      revokedReason: null,
    };
    await this.backing.put("sessions", session);
    this._audit("Info", "Identity", "SessionCreated", `Session created for ${personName} (${role}).`, {
      sessionId: session.id,
      deviceId,
      role,
      expiresAtUtc: session.expiresAtUtc,
    });
    return { ok: true, session, token };
  }

  async listSessions() {
    return this.backing.all("sessions");
  }

  async findSessionByToken(token) {
    const hash = await sha256B64url(token);
    const sessions = await this.listSessions();
    return sessions.find((session) => session.tokenHash === hash) ?? null;
  }

  async validateSession(token, deviceId = null) {
    if (!token) return { ok: false, reason: "NO_SESSION_TOKEN" };
    const session = await this.findSessionByToken(token);
    if (!session) return { ok: false, reason: "SESSION_NOT_FOUND" };
    if (deviceId && session.deviceId !== deviceId) return { ok: false, reason: "SESSION_DEVICE_MISMATCH" };
    if (session.revoked) return { ok: false, reason: "SESSION_REVOKED", session };
    const device = await this.backing.get("devices", session.deviceId);
    if (!device) return { ok: false, reason: "DEVICE_NOT_FOUND", session };
    if (device.revoked) return { ok: false, reason: "DEVICE_REVOKED", session };
    if (Date.parse(session.expiresAtUtc) <= this.clock.now().getTime()) {
      return { ok: false, reason: "SESSION_EXPIRED", session };
    }
    return { ok: true, session, device };
  }

  async revokeSession(sessionId, reason) {
    const session = await this.backing.get("sessions", sessionId);
    if (!session) return { ok: false, reason: "SESSION_NOT_FOUND" };
    session.revoked = true;
    session.revokedAtUtc = this.clock.now().toISOString();
    session.revokedReason = reason;
    await this.backing.put("sessions", session);
    for (const lease of await this.listLeases()) {
      if (lease.sessionId === sessionId && !lease.revoked) {
        lease.revoked = true;
        lease.revokedReason = `session-revoked:${reason}`;
        await this.backing.put("leases", lease);
      }
    }
    this._audit("Security", "Identity", "SessionRevoked", `Session revoked: ${reason}.`, { sessionId, reason });
    return { ok: true, session };
  }

  async revokeAllSessions(reason) {
    const sessions = await this.listSessions();
    for (const session of sessions) {
      if (!session.revoked) await this.revokeSession(session.id, reason);
    }
    return sessions.length;
  }

  async issueLease({
    sessionId,
    capabilities,
    resourceScopes,
    riskCeiling,
    ttlMs = 60 * 1000,
    maxIterations = 8,
    constraints = {},
    metadata = {},
  }) {
    const session = await this.backing.get("sessions", sessionId);
    if (!session) return { ok: false, reason: "SESSION_NOT_FOUND" };
    if (session.revoked) return { ok: false, reason: "SESSION_REVOKED" };
    const now = this.clock.now().getTime();
    const lease = {
      id: randomId("lease"),
      sessionId,
      capabilities: [...capabilities],
      resourceScopes: [...resourceScopes],
      riskCeiling,
      maxIterations,
      constraints,
      metadata,
      issuedAtUtc: new Date(now).toISOString(),
      expiresAtUtc: new Date(now + ttlMs).toISOString(),
      revoked: false,
      revokedReason: null,
      consumedAuthorizations: [],
    };
    await this.backing.put("leases", lease);
    this._audit("Info", "Authorization", "LeaseIssued", "Short-lived authorization lease issued.", {
      leaseId: lease.id,
      sessionId,
      capabilities: lease.capabilities.join(","),
      riskCeiling,
      expiresAtUtc: lease.expiresAtUtc,
    });
    return { ok: true, lease };
  }

  async listLeases() {
    return this.backing.all("leases");
  }

  async getLease(leaseId) {
    return this.backing.get("leases", leaseId);
  }

  async validateLease(leaseId) {
    const lease = await this.backing.get("leases", leaseId);
    if (!lease) return { ok: false, reason: "LEASE_NOT_FOUND" };
    if (lease.revoked) return { ok: false, reason: "LEASE_REVOKED", lease };
    if (Date.parse(lease.expiresAtUtc) <= this.clock.now().getTime()) return { ok: false, reason: "LEASE_EXPIRED", lease };
    const session = await this.backing.get("sessions", lease.sessionId);
    if (!session) return { ok: false, reason: "SESSION_NOT_FOUND", lease };
    if (session.revoked) return { ok: false, reason: "SESSION_REVOKED", lease };
    const device = await this.backing.get("devices", session.deviceId);
    if (!device || device.revoked) return { ok: false, reason: "DEVICE_REVOKED", lease };
    return { ok: true, lease, session, device };
  }

  async consumeAuthorization(leaseId, authorizationId) {
    const lease = await this.backing.get("leases", leaseId);
    if (!lease) return { ok: false, reason: "LEASE_NOT_FOUND" };
    if (lease.consumedAuthorizations.includes(authorizationId)) return { ok: false, reason: "AUTHORIZATION_ALREADY_CONSUMED" };
    lease.consumedAuthorizations.push(authorizationId);
    await this.backing.put("leases", lease);
    return { ok: true };
  }

  async revokeLease(leaseId, reason) {
    const lease = await this.backing.get("leases", leaseId);
    if (!lease) return { ok: false, reason: "LEASE_NOT_FOUND" };
    lease.revoked = true;
    lease.revokedReason = reason;
    await this.backing.put("leases", lease);
    return { ok: true, lease };
  }

  async createEnrollmentToken({ agentId, ttlMs = 10 * 60 * 1000 }) {
    const token = randomToken(24);
    const now = this.clock.now().getTime();
    const record = {
      id: await sha256B64url(token),
      agentId,
      issuedAtUtc: new Date(now).toISOString(),
      expiresAtUtc: new Date(now + ttlMs).toISOString(),
      used: false,
      token,
    };
    await this.backing.put("enrollmentTokens", record);
    this._audit("Info", "Enrollment", "EnrollmentTokenIssued", "Single-use agent enrollment token issued.", {
      agentId,
      expiresAtUtc: record.expiresAtUtc,
    });
    return { ok: true, token, record };
  }

  async consumeEnrollmentToken(token, agentId) {
    if (!token) return { ok: false, reason: "ENROLLMENT_TOKEN_MISSING" };
    const record = await this.backing.get("enrollmentTokens", await sha256B64url(token));
    if (!record) {
      this._audit("Security", "Enrollment", "EnrollmentTokenRejected", "Enrollment rejected: unknown token.", { agentId });
      return { ok: false, reason: "ENROLLMENT_TOKEN_UNKNOWN" };
    }
    if (record.used) {
      this._audit("Security", "Enrollment", "EnrollmentTokenRejected", "Enrollment rejected: token already used.", { agentId });
      return { ok: false, reason: "ENROLLMENT_TOKEN_REUSED" };
    }
    if (record.agentId !== agentId) {
      this._audit("Security", "Enrollment", "EnrollmentTokenRejected", "Enrollment rejected: token bound to a different agent.", {
        agentId,
      });
      return { ok: false, reason: "ENROLLMENT_TOKEN_MISMATCH" };
    }
    if (Date.parse(record.expiresAtUtc) <= this.clock.now().getTime()) {
      this._audit("Security", "Enrollment", "EnrollmentTokenRejected", "Enrollment rejected: token expired.", { agentId });
      return { ok: false, reason: "ENROLLMENT_TOKEN_EXPIRED" };
    }
    record.used = true;
    await this.backing.put("enrollmentTokens", record);
    return { ok: true, record };
  }

  async recordRevocation({ subjectType, subjectId, reason, appliedAtUtc = null }) {
    const revocation = {
      id: randomId("rev"),
      subjectType,
      subjectId,
      reason,
      issuedAtUtc: this.clock.now().toISOString(),
      appliedAtUtc,
    };
    await this.backing.put("revocations", revocation);
    return revocation;
  }

  async listRevocations() {
    return this.backing.all("revocations");
  }

  async setSetting(id, value) {
    await this.backing.put("settings", { id, value });
  }

  async getSetting(id) {
    const record = await this.backing.get("settings", id);
    return record ? record.value : null;
  }

  lockSecrets(reason) {
    this.secretsAvailable = false;
    this._audit("Critical", "Secrets", "SecretsUnavailable", "Protected secrets made unavailable to the locked process.", { reason });
  }

  unlockSecrets() {
    this.secretsAvailable = true;
  }
}

export function describeIdentityBacking(store) {
  return {
    persistent: store.persistent,
    backing: store.persistent ? "IndexedDB" : "in-memory",
    installId: store.install?.installId ?? null,
    keyId: store.install?.kid ?? null,
    algorithm: store.install?.algorithm ?? null,
    privateKeyExtractable: store.install?.privateKeyExtractable ?? null,
    keyStorage: store.install?.keyStorage ?? null,
  };
}
