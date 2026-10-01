export class FileSystemViolation extends Error {
  constructor(code, message) {
    super(message);
    this.name = "FileSystemViolation";
    this.code = code;
  }
}

function canonical(path) {
  const normalized = String(path ?? "").trim().replace(/\\/g, "/").replace(/\/+/g, "/").replace(/\/$/, "");
  return normalized === "" ? "/" : normalized;
}

function keyOf(path) {
  return canonical(path).toLowerCase();
}

export class VirtualFileSystem {
  constructor({ clock, label = "windows-11-agent" } = {}) {
    this.clock = clock;
    this.label = label;
    this.entries = new Map();
    this.mkdir("/");
  }

  static key(path) {
    return keyOf(path);
  }

  mkdir(path, { actor = "system" } = {}) {
    const display = canonical(path);
    const key = keyOf(display);
    if (this.entries.has(key)) return this.entries.get(key);
    const parentKey = keyOf(display.split("/").slice(0, -1).join("/") || "/");
    const stats = {
      key,
      path: display,
      type: "directory",
      content: null,
      size: 0,
      createdAtUtc: this.clock.now().toISOString(),
      updatedAtUtc: this.clock.now().toISOString(),
      updatedBy: actor,
    };
    this.entries.set(key, stats);
    if (parentKey !== key && !this.entries.has(parentKey)) {
      this.mkdir(display.split("/").slice(0, -1).join("/") || "/", { actor });
    }
    return stats;
  }

  ensureDirectoryFor(path, { actor }) {
    const segments = canonical(path).split("/").slice(0, -1);
    let current = "";
    for (const segment of segments) {
      current = current === "" && segment === "" ? "" : `${current}/${segment}`;
      if (current !== "") this.mkdir(current, { actor });
    }
  }

  writeFile(path, content, { actor = "unknown" } = {}) {
    const display = canonical(path);
    const key = keyOf(display);
    this.ensureDirectoryFor(display, { actor });
    const existing = this.entries.get(key);
    const stats = {
      key,
      path: existing?.path ?? display,
      type: "file",
      content: String(content ?? ""),
      size: String(content ?? "").length,
      createdAtUtc: existing?.createdAtUtc ?? this.clock.now().toISOString(),
      updatedAtUtc: this.clock.now().toISOString(),
      updatedBy: actor,
    };
    this.entries.set(key, stats);
    return stats;
  }

  readFile(path) {
    const stats = this.entries.get(keyOf(path));
    if (!stats || stats.type !== "file") throw new FileSystemViolation("NOT_FOUND", `file not found: ${path}`);
    return stats.content;
  }

  stat(path) {
    return this.entries.get(keyOf(path)) ?? null;
  }

  exists(path) {
    return this.entries.has(keyOf(path));
  }

  list(path) {
    const parentKey = keyOf(path);
    const children = [...this.entries.values()].filter((entry) => {
      if (entry.key === parentKey) return false;
      const parent = keyOf(entry.path.split("/").slice(0, -1).join("/") || "/");
      if (parentKey === "/") return entry.path.split("/").filter(Boolean).length === 1;
      return parent === parentKey;
    });
    return children.sort((a, b) => a.path.localeCompare(b.path));
  }

  remove(path) {
    const key = keyOf(path);
    const removed = [];
    for (const entryKey of [...this.entries.keys()]) {
      if (entryKey === key || entryKey.startsWith(`${key}/`)) {
        removed.push(this.entries.get(entryKey).path);
        this.entries.delete(entryKey);
      }
    }
    return removed;
  }

  snapshot() {
    const snapshot = {};
    for (const [key, entry] of this.entries) {
      snapshot[key] = {
        path: entry.path,
        type: entry.type,
        size: entry.size,
        updatedBy: entry.updatedBy,
        updatedAtUtc: entry.updatedAtUtc,
        content: entry.content,
      };
    }
    return snapshot;
  }

  diff(before) {
    const after = this.snapshot();
    const added = [];
    const modified = [];
    const removed = [];
    for (const [key, entry] of Object.entries(after)) {
      const previous = before[key];
      if (!previous) {
        added.push(entry.path);
      } else if (previous.content !== entry.content || previous.type !== entry.type) {
        modified.push(entry.path);
      }
    }
    for (const [key, entry] of Object.entries(before)) {
      if (!(key in after)) removed.push(entry.path);
    }
    return {
      added: added.sort(),
      modified: modified.sort(),
      removed: removed.sort(),
      totalChanges: added.length + modified.length + removed.length,
    };
  }

  fileCount() {
    return [...this.entries.values()].filter((entry) => entry.type === "file").length;
  }
}
