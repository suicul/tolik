import { redactRecord, redactText, scanForSecrets } from "./secrets.js";
import { randomId } from "./clock.js";

export const LogLevel = Object.freeze({
  Info: "Info",
  Warning: "Warning",
  Security: "Security",
  Critical: "Critical",
});

const LEVEL_ORDER = { Info: 0, Warning: 1, Security: 2, Critical: 3 };

export class AuditLog {
  constructor({ clock, consoleSink = true, maxEvents = 2000, sinkName = "tolik-core" } = {}) {
    this.clock = clock;
    this.consoleSink = consoleSink;
    this.maxEvents = maxEvents;
    this.sinkName = sinkName;
    this._events = [];
    this._listeners = new Set();
    this._capturedSinkText = [];
    this._counters = new Map();
  }

  subscribe(listener) {
    this._listeners.add(listener);
    return () => this._listeners.delete(listener);
  }

  record(entry = {}) {
    const level = entry.level ?? LogLevel.Info;
    const message = String(entry.message ?? "");
    const redactedMessage = redactText(message);
    const redactedTemplate = entry.messageTemplate ? redactText(String(entry.messageTemplate)) : null;
    const properties = redactRecord(entry.properties ?? {});
    const scopes = redactRecord(entry.scopes ?? {});
    const exceptionText = entry.exception ? redactText(String(entry.exception)).text : null;
    const event = {
      eventId: entry.eventId ?? randomId("evt"),
      timestampUtc: (entry.timestampUtc ?? this.clock.now().toISOString()),
      level,
      category: entry.category ?? "General",
      eventName: entry.eventName ?? "UnnamedEvent",
      message: redactedMessage.text,
      messageTemplate: redactedTemplate ? redactedTemplate.text : null,
      properties,
      scopes,
      exception: exceptionText,
      redactionCount: redactedMessage.redactions + (redactedTemplate?.redactions ?? 0),
      sequence: this._events.length + 1,
    };
    this._events.push(event);
    if (this._events.length > this.maxEvents) this._events.shift();
    const key = `${event.category}:${event.eventName}`;
    this._counters.set(key, (this._counters.get(key) ?? 0) + 1);
    this._emitSink(event);
    for (const listener of this._listeners) {
      try {
        listener(event);
      } catch {
        /* listener failures must never break auditing */
      }
    }
    return event;
  }

  _emitSink(event) {
    const serialized = JSON.stringify({
      timestamp: event.timestampUtc,
      level: event.level,
      category: event.category,
      eventId: event.eventName,
      correlationId: event.scopes?.correlationId ?? event.scopes?.taskId ?? null,
      message: event.message,
      messageTemplate: event.messageTemplate,
      properties: event.properties,
      scopes: event.scopes,
      exception: event.exception,
    });
    this._capturedSinkText.push(serialized);
    if (this.consoleSink) {
      const method = event.level === LogLevel.Critical || event.level === LogLevel.Security ? "warn" : "log";
      console[method](`[${this.sinkName}] ${serialized}`);
    }
  }

  info(category, eventName, message, properties = {}, scopes = {}) {
    return this.record({ level: LogLevel.Info, category, eventName, message, properties, scopes });
  }

  warning(category, eventName, message, properties = {}, scopes = {}) {
    return this.record({ level: LogLevel.Warning, category, eventName, message, properties, scopes });
  }

  security(category, eventName, message, properties = {}, scopes = {}) {
    return this.record({ level: LogLevel.Security, category, eventName, message, properties, scopes });
  }

  critical(category, eventName, message, properties = {}, scopes = {}) {
    return this.record({ level: LogLevel.Critical, category, eventName, message, properties, scopes });
  }

  events({ minLevel = LogLevel.Info, category = null } = {}) {
    return this._events.filter(
      (event) => LEVEL_ORDER[event.level] >= LEVEL_ORDER[minLevel] && (category === null || event.category === category),
    );
  }

  counts() {
    return Object.fromEntries(this._counters);
  }

  capturedSinkText() {
    return this._capturedSinkText.join("\n");
  }

  inspectForSecret(sentinel) {
    if (typeof sentinel !== "string" || sentinel.length === 0) return { present: false, sinks: [] };
    const sinks = [];
    if (this.capturedSinkText().includes(sentinel)) sinks.push("console-sink");
    if (JSON.stringify(this._events).includes(sentinel)) sinks.push("audit-store");
    return { present: sinks.length > 0, sinks };
  }

  scanStoredEvents() {
    return scanForSecrets(JSON.stringify(this._events));
  }

  export() {
    return {
      sinkName: this.sinkName,
      generatedAtUtc: this.clock.now().toISOString(),
      eventCount: this._events.length,
      events: this._events,
    };
  }

  clear() {
    this._events = [];
    this._capturedSinkText = [];
    this._counters = new Map();
  }
}
