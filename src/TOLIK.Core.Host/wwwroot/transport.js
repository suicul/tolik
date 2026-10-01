import { canonicalJson, parseStrict, StrictJsonError } from "./strict-json.js";
import { randomId, utf8, base64url } from "./clock.js";
import { parseEnvelope } from "./contracts.js";

export class NonceStore {
  constructor({ clock, ttlMs = 5 * 60 * 1000 } = {}) {
    this.clock = clock;
    this.ttlMs = ttlMs;
    this._consumed = new Map();
  }

  has(nonce) {
    this.sweep();
    return this._consumed.has(nonce);
  }

  consume(nonce, expiresAtMs = this.clock.nowMs() + this.ttlMs) {
    this.sweep();
    if (this._consumed.has(nonce)) return false;
    this._consumed.set(nonce, expiresAtMs);
    return true;
  }

  sweep() {
    const now = this.clock.nowMs();
    for (const [nonce, expiry] of this._consumed) {
      if (expiry <= now) this._consumed.delete(nonce);
    }
  }

  get size() {
    return this._consumed.size;
  }
}

export class PeerRegistry {
  constructor() {
    this._keys = new Map();
  }

  pin(peerId, { kid, publicJwk, role, displayName }) {
    this._keys.set(peerId, { kid, publicJwk, role, displayName, pinnedAtUtc: new Date().toISOString() });
    return this;
  }

  unpin(peerId) {
    this._keys.delete(peerId);
  }

  resolve(peerId) {
    return this._keys.get(peerId) ?? null;
  }

  list() {
    return [...this._keys.entries()].map(([peerId, record]) => ({ peerId, ...record }));
  }
}

export class SignedTransport {
  constructor({ identity, clock, audit, frameTtlMs = 30000, audience }) {
    this.identity = identity;
    this.clock = clock;
    this.audit = audit;
    this.frameTtlMs = frameTtlMs;
    this.audience = audience;
    this.nonces = new NonceStore({ clock });
    this.sentFrames = 0;
    this.rejectedFrames = 0;
  }

  async seal(envelope, { audience = this.audience } = {}) {
    const serialized = canonicalJson(envelope);
    const nonce = randomId("nonce");
    const issuedAtMs = this.clock.nowMs();
    const header = {
      frameVersion: "v1",
      audience,
      signerKid: this.identity.install.kid,
      algorithm: this.identity.install.algorithm,
      nonce,
      issuedAtUtc: new Date(issuedAtMs).toISOString(),
      expiresAtUtc: new Date(issuedAtMs + this.frameTtlMs).toISOString(),
      payloadDigest: serialized,
    };
    const signature = await this.identity.sign(utf8(canonicalJson(header)));
    this.sentFrames += 1;
    return {
      frameVersion: header.frameVersion,
      audience,
      signerKid: header.signerKid,
      algorithm: header.algorithm,
      nonce,
      issuedAtUtc: header.issuedAtUtc,
      expiresAtUtc: header.expiresAtUtc,
      payload: serialized,
      signature: signature.signature,
    };
  }

  async open(frame, { expectedAudience, resolvePublicKey, requireSignature = true } = {}) {
    const reject = (code, detail, extra = {}) => {
      this.rejectedFrames += 1;
      this.audit?.record({
        level: "Security",
        category: "Transport",
        eventName: "FrameRejected",
        message: `Inbound frame rejected: ${detail}`,
        properties: { code, ...extra },
      });
      return { ok: false, code, detail, ...extra };
    };

    if (!frame || typeof frame !== "object") return reject("MALFORMED_FRAME", "frame is not an object");
    for (const field of ["audience", "signerKid", "algorithm", "nonce", "issuedAtUtc", "expiresAtUtc", "payload", "signature"]) {
      if (typeof frame[field] !== "string" || frame[field].length === 0) {
        return reject("MALFORMED_FRAME", `frame field '${field}' is missing or not a string`);
      }
    }
    if (expectedAudience && frame.audience !== expectedAudience) {
      return reject("AUDIENCE_MISMATCH", `frame was addressed to '${frame.audience}', not '${expectedAudience}'`);
    }
    const expiresAtMs = Date.parse(frame.expiresAtUtc);
    if (!Number.isFinite(expiresAtMs)) return reject("MALFORMED_FRAME", "expiresAtUtc is not a valid timestamp");
    if (expiresAtMs <= this.clock.nowMs()) {
      return reject("FRAME_EXPIRED", `frame expired at ${frame.expiresAtUtc}`);
    }

    let envelopeRaw;
    try {
      envelopeRaw = parseStrict(frame.payload);
    } catch (error) {
      if (error instanceof StrictJsonError) {
        const code = /duplicate property/.test(error.message) ? "DUPLICATE_PROPERTY" : "MALFORMED_JSON";
        return reject(code, error.message);
      }
      throw error;
    }

    if (requireSignature) {
      const peer = resolvePublicKey ? resolvePublicKey(frame.signerKid) : null;
      if (!peer) return reject("UNKNOWN_SIGNER", `no trusted public key is pinned for signer '${frame.signerKid}'`);
      const header = {
        frameVersion: frame.frameVersion ?? "v1",
        audience: frame.audience,
        signerKid: frame.signerKid,
        algorithm: frame.algorithm,
        nonce: frame.nonce,
        issuedAtUtc: frame.issuedAtUtc,
        expiresAtUtc: frame.expiresAtUtc,
        payloadDigest: frame.payload,
      };
      const valid = await this.identity.verifyWithPublicJwk(peer.publicJwk, utf8(canonicalJson(header)), frame.signature);
      if (!valid) return reject("INVALID_SIGNATURE", "frame signature does not verify against the pinned peer key");
      if (!this.nonces.consume(frame.nonce, expiresAtMs)) {
        return reject("REPLAY_DETECTED", `nonce '${frame.nonce}' was already consumed`);
      }
    }

    const parsed = parseEnvelope(frame.payload);
    if (!parsed.ok) {
      const primary = parsed.errors[0];
      return reject(primary.code, primary.message, { validationErrors: parsed.errors });
    }

    this.audit?.record({
      level: "Info",
      category: "Transport",
      eventName: "FrameAccepted",
      message: `Frame '${frame.nonce}' verified and accepted.`,
      properties: { signerKid: frame.signerKid, correlationId: envelopeRaw.correlationId, messageType: envelopeRaw.messageType },
    });
    return { ok: true, envelope: parsed.value, nonce: frame.nonce, signerKid: frame.signerKid };
  }

  static tamper(frame, mutate) {
    const copy = JSON.parse(JSON.stringify(frame));
    mutate(copy);
    return copy;
  }
}

export function describeTransport(transport, peerRegistry) {
  return {
    audience: transport.audience,
    frameTtlMs: transport.frameTtlMs,
    sentFrames: transport.sentFrames,
    rejectedFrames: transport.rejectedFrames,
    consumedNonces: transport.nonces.size,
    pinnedPeers: peerRegistry ? peerRegistry.list().map((peer) => ({ peerId: peer.peerId, kid: peer.kid })) : [],
  };
}
