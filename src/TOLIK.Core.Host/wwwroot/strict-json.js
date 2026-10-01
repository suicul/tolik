export class StrictJsonError extends Error {
  constructor(message) {
    super(message);
    this.name = "StrictJsonError";
  }
}

const MAX_DEPTH = 64;
const FORBIDDEN_KEYS = new Set(["__proto__", "constructor", "prototype"]);

function isWhitespace(ch) {
  return ch === " " || ch === "\t" || ch === "\n" || ch === "\r";
}

const NUMBER_RE = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?/;
const DELIMITERS = new Set([" ", "\t", "\n", "\r", ",", "}", "]", ""]);

class Parser {
  constructor(text) {
    this.text = text;
    this.i = 0;
    this.depth = 0;
  }

  atEnd() {
    return this.i >= this.text.length;
  }

  fail(message) {
    throw new StrictJsonError(`${message} at offset ${this.i}`);
  }

  skipWhitespace() {
    while (!this.atEnd() && isWhitespace(this.text[this.i])) this.i += 1;
  }

  parseValue() {
    if (this.depth > MAX_DEPTH) this.fail("maximum nesting depth exceeded");
    const ch = this.text[this.i];
    if (ch === "{") return this.parseObject();
    if (ch === "[") return this.parseArray();
    if (ch === '"') return this.parseString();
    if (ch === "t") return this.parseKeyword("true", true);
    if (ch === "f") return this.parseKeyword("false", false);
    if (ch === "n") return this.parseKeyword("null", null);
    if (ch === "-" || (ch >= "0" && ch <= "9")) return this.parseNumber();
    this.fail(`unexpected character ${JSON.stringify(ch)}`);
  }

  parseKeyword(word, value) {
    if (!this.text.startsWith(word, this.i)) this.fail(`invalid literal, expected ${word}`);
    this.i += word.length;
    return value;
  }

  parseObject() {
    this.i += 1;
    this.depth += 1;
    const result = {};
    const seen = new Set();
    this.skipWhitespace();
    if (this.text[this.i] === "}") {
      this.i += 1;
      this.depth -= 1;
      return result;
    }
    for (;;) {
      this.skipWhitespace();
      if (this.text[this.i] !== '"') this.fail("expected property name");
      const key = this.parseString();
      if (seen.has(key)) this.fail(`duplicate property ${JSON.stringify(key)}`);
      if (FORBIDDEN_KEYS.has(key)) this.fail(`forbidden property name ${JSON.stringify(key)}`);
      seen.add(key);
      this.skipWhitespace();
      if (this.text[this.i] !== ":") this.fail("expected ':' after property name");
      this.i += 1;
      this.skipWhitespace();
      result[key] = this.parseValue();
      this.skipWhitespace();
      const delimiter = this.text[this.i];
      if (delimiter === ",") {
        this.i += 1;
        continue;
      }
      if (delimiter === "}") {
        this.i += 1;
        break;
      }
      this.fail("expected ',' or '}'");
    }
    this.depth -= 1;
    return result;
  }

  parseArray() {
    this.i += 1;
    this.depth += 1;
    const result = [];
    this.skipWhitespace();
    if (this.text[this.i] === "]") {
      this.i += 1;
      this.depth -= 1;
      return result;
    }
    for (;;) {
      this.skipWhitespace();
      result.push(this.parseValue());
      this.skipWhitespace();
      const delimiter = this.text[this.i];
      if (delimiter === ",") {
        this.i += 1;
        continue;
      }
      if (delimiter === "]") {
        this.i += 1;
        break;
      }
      this.fail("expected ',' or ']'");
    }
    this.depth -= 1;
    return result;
  }

  parseString() {
    this.i += 1;
    let out = "";
    for (;;) {
      if (this.atEnd()) this.fail("unterminated string");
      const ch = this.text[this.i];
      if (ch === '"') {
        this.i += 1;
        return out;
      }
      if (ch === "\\") {
        this.i += 1;
        const esc = this.text[this.i];
        if (esc === "u") {
          const hex = this.text.slice(this.i + 1, this.i + 5);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) this.fail("invalid unicode escape");
          out += String.fromCharCode(parseInt(hex, 16));
          this.i += 5;
          continue;
        }
        const simple = { '"': '"', "\\": "\\", "/": "/", b: "\b", f: "\f", n: "\n", r: "\r", t: "\t" };
        if (!(esc in simple)) this.fail(`invalid escape ${JSON.stringify(esc)}`);
        out += simple[esc];
        this.i += 1;
        continue;
      }
      if (ch.charCodeAt(0) < 0x20) this.fail("unescaped control character in string");
      out += ch;
      this.i += 1;
    }
  }

  parseNumber() {
    const match = NUMBER_RE.exec(this.text.slice(this.i));
    if (!match) this.fail("invalid number");
    const raw = match[0];
    const next = this.text[this.i + raw.length];
    if (next !== undefined && !DELIMITERS.has(next)) this.fail(`invalid number terminator ${JSON.stringify(next)}`);
    this.i += raw.length;
    const value = Number(raw);
    if (!Number.isFinite(value)) this.fail("non-finite number");
    return value;
  }
}

export function parseStrict(text) {
  if (typeof text !== "string") throw new StrictJsonError("input must be a string");
  const parser = new Parser(text);
  parser.skipWhitespace();
  if (parser.atEnd()) throw new StrictJsonError("empty input is not valid JSON");
  const value = parser.parseValue();
  parser.skipWhitespace();
  if (!parser.atEnd()) throw new StrictJsonError(`unexpected trailing content at offset ${parser.i}`);
  return value;
}

export function canonicalJson(value) {
  if (value === null || typeof value === "boolean" || typeof value === "string") return JSON.stringify(value);
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new StrictJsonError("non-finite number cannot be serialized");
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (typeof value === "object") {
    const keys = Object.keys(value).sort();
    const parts = keys.map((k) => `${JSON.stringify(k)}:${canonicalJson(value[k])}`);
    return `{${parts.join(",")}}`;
  }
  throw new StrictJsonError(`unsupported value type ${typeof value}`);
}

export function deepClone(value) {
  return value === undefined ? undefined : parseStrict(JSON.stringify(value));
}
