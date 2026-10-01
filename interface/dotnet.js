const DECLARATION_RE =
  /\b(?:public|internal|private|protected)\s+(?:static\s+|async\s+|sealed\s+|override\s+|virtual\s+|partial\s+|extern\s+)*([A-Za-z_][\w<>\[\],?.]*)\s+([A-Za-z_]\w*)\s*\(/g;

const CALL_RE = /\b([A-Z]\w*)\.([A-Za-z_]\w*)\s*\(/g;

const KEYWORDS = new Set([
  "if",
  "for",
  "foreach",
  "while",
  "switch",
  "catch",
  "using",
  "lock",
  "return",
  "new",
  "throw",
  "await",
  "var",
  "sizeof",
  "typeof",
  "nameof",
  "default",
]);

function lineColOf(source, offset) {
  const before = source.slice(0, offset);
  const line = before.split("\n").length;
  const lastNewline = before.lastIndexOf("\n");
  const column = offset - lastNewline;
  return { line, column };
}

function findTypeBodies(source) {
  const bodies = [];
  const typeRe = /\b(?:class|record|struct|interface)\s+([A-Za-z_]\w*)/g;
  let match;
  while ((match = typeRe.exec(source)) !== null) {
    const braceStart = source.indexOf("{", match.index);
    if (braceStart === -1) continue;
    let depth = 0;
    let index = braceStart;
    for (; index < source.length; index += 1) {
      if (source[index] === "{") depth += 1;
      else if (source[index] === "}") {
        depth -= 1;
        if (depth === 0) break;
      }
    }
    bodies.push({ name: match[1], start: braceStart, end: index, body: source.slice(braceStart, index + 1) });
  }
  return bodies;
}

export function collectTypes(vfs, { projectRoot = "" } = {}) {
  const types = new Map();
  const rootKey = projectRoot.replace(/\\/g, "/").toLowerCase();
  for (const entry of vfs.entries.values()) {
    if (entry.type !== "file" || !entry.path.toLowerCase().endsWith(".cs")) continue;
    if (rootKey && !entry.key.startsWith(rootKey)) continue;
    const source = entry.content ?? "";
    for (const body of findTypeBodies(source)) {
      const methods = new Set();
      DECLARATION_RE.lastIndex = 0;
      let declaration;
      while ((declaration = DECLARATION_RE.exec(body.body)) !== null) {
        const returnType = declaration[1];
        const methodName = declaration[2];
        if (KEYWORDS.has(methodName) || KEYWORDS.has(returnType)) continue;
        methods.add(methodName);
      }
      const existing = types.get(body.name);
      if (existing) {
        for (const method of methods) existing.methods.add(method);
      } else {
        types.set(body.name, { name: body.name, methods, path: entry.path });
      }
    }
  }
  return types;
}

export function analyzeWorkspace(vfs, { projectRoot = "" } = {}) {
  const types = collectTypes(vfs, { projectRoot });
  const diagnostics = [];
  const rootKey = projectRoot.replace(/\\/g, "/").toLowerCase();
  for (const entry of vfs.entries.values()) {
    if (entry.type !== "file" || !entry.path.toLowerCase().endsWith(".cs")) continue;
    if (rootKey && !entry.key.startsWith(rootKey)) continue;
    const source = entry.content ?? "";
    const openBraces = (source.match(/\{/g) ?? []).length;
    const closeBraces = (source.match(/\}/g) ?? []).length;
    if (openBraces !== closeBraces) {
      diagnostics.push({
        code: "CS1513",
        severity: "error",
        file: entry.path,
        line: source.split("\n").length,
        column: 1,
        message: "} expected",
      });
    }
    CALL_RE.lastIndex = 0;
    let call;
    while ((call = CALL_RE.exec(source)) !== null) {
      const [raw, typeName, methodName] = call;
      const declared = types.get(typeName);
      if (!declared) continue;
      if (declared.methods.has(methodName)) continue;
      const position = lineColOf(source, call.index);
      diagnostics.push({
        code: "CS0117",
        severity: "error",
        file: entry.path,
        line: position.line,
        column: position.column,
        message: `'${typeName}' does not contain a definition for '${methodName}'`,
      });
    }
  }
  diagnostics.sort((a, b) => (a.file === b.file ? a.line - b.line : a.file.localeCompare(b.file)));
  return { types: [...types.values()].map((t) => ({ name: t.name, methods: [...t.methods].sort(), path: t.path })), diagnostics };
}

export function collectTestMethods(vfs, { projectRoot = "" } = {}) {
  const tests = [];
  const rootKey = projectRoot.replace(/\\/g, "/").toLowerCase();
  for (const entry of vfs.entries.values()) {
    if (entry.type !== "file" || !/Tests\.cs$/i.test(entry.path)) continue;
    if (rootKey && !entry.key.startsWith(rootKey)) continue;
    const source = entry.content ?? "";
    const methodRe = /\[(Fact|Theory)\][\s\S]{0,200}?public\s+(?:async\s+)?(?:Task|void)\s+([A-Za-z_]\w*)\s*\(/g;
    let match;
    while ((match = methodRe.exec(source)) !== null) {
      tests.push({ name: match[2], file: entry.path, requiresBuild: !/RejectsEmptyFrame|RejectsMalformed/.test(match[2]) });
    }
  }
  return tests;
}

export function runBuild(vfs, { projectRoot, clock, correlationId = "" } = {}) {
  const lines = [];
  lines.push(`MSBuild version 17.11.4+9a5b0a2c7 for .NET`);
  lines.push(`  Determining projects to restore...`);
  const projects = [...vfs.entries.values()]
    .filter((entry) => entry.type === "file" && /\.csproj$/i.test(entry.path) && entry.key.startsWith(projectRoot.toLowerCase()))
    .map((entry) => entry.path);
  for (const project of projects) lines.push(`  Restored ${project.replace(/\//g, "\\")} (in 41 ms).`);
  lines.push(`  ${projects.length} of ${projects.length} projects are up-to-date for restore.`);
  const { diagnostics } = analyzeWorkspace(vfs, { projectRoot });
  for (const project of projects) {
    const projectFile = project.split("/").pop();
    const windowsPath = project.replace(/\//g, "\\");
    const directory = windowsPath.slice(0, windowsPath.lastIndexOf("\\") + 1);
    lines.push(`  ${projectFile.replace(".csproj", "")} -> ${directory}bin\\Debug\\net10.0\\${projectFile.replace(".csproj", ".dll")}`);
  }
  for (const diagnostic of diagnostics) {
    lines.push(
      `${diagnostic.file.replace(/\//g, "\\")}(${diagnostic.line},${diagnostic.column}): ${diagnostic.severity} ${diagnostic.code}: ${diagnostic.message} [${projects[0] ? projects[0].replace(/\//g, "\\") : ""}]`,
    );
  }
  lines.push("");
  lines.push(`    0 Warning(s)`);
  lines.push(`    ${diagnostics.length} Error(s)`);
  lines.push("");
  lines.push(diagnostics.length === 0 ? "Build succeeded." : "Build FAILED.");
  const stdout = lines.join("\n");
  const durationMs = 820 + diagnostics.length * 137 + projects.length * 96;
  return {
    status: diagnostics.length === 0 ? "Succeeded" : "Failed",
    exitCode: diagnostics.length === 0 ? 0 : 1,
    stdout,
    stderr: "",
    durationMs,
    diagnostics,
    simulatedCompletionAtUtc: new Date(clock.nowMs() + durationMs).toISOString(),
  };
}

export function runTests(vfs, { projectRoot, clock, buildDiagnostics } = {}) {
  const tests = collectTestMethods(vfs, { projectRoot });
  const lines = [];
  lines.push("Microsoft (R) Test Execution Command Line Tool Version 17.11.0 (x64)");
  lines.push("Copyright (c) Microsoft Corporation.  All rights reserved.");
  lines.push("");
  lines.push("Starting test execution, please wait...");
  lines.push("A total of 1 test files matched the specified pattern.");
  lines.push("");
  const results = [];
  for (const test of tests) {
    const passed = test.requiresBuild ? (buildDiagnostics ?? []).length === 0 : true;
    results.push({ ...test, passed });
    if (passed) {
      lines.push(`  Passed ${test.name} [${20 + test.name.length} ms]`);
    } else {
      lines.push(`  Failed ${test.name} [${18 + test.name.length} ms]`);
      lines.push("  Error Message:");
      const first = (buildDiagnostics ?? [])[0];
      lines.push(`   Kip.Parser assembly could not be loaded: ${first ? `${first.code} ${first.message}` : "compilation failed"}`);
      lines.push("  Stack Trace:");
      lines.push(`   at Kip.Parser.KipParser.Parse(String raw) in ${test.file.replace(/\//g, "\\")}:line 12`);
      lines.push("");
    }
  }
  const passedCount = results.filter((result) => result.passed).length;
  const failedCount = results.length - passedCount;
  lines.push("");
  lines.push(`Failed!  - Failed:     ${failedCount}, Passed:     ${passedCount}, Skipped:      0, Total:     ${results.length}, Duration: ${230 + results.length * 41} ms`);
  const durationMs = 230 + results.length * 41;
  return {
    status: failedCount === 0 ? "Succeeded" : "Failed",
    exitCode: failedCount === 0 ? 0 : 1,
    stdout: lines.join("\n"),
    stderr: "",
    durationMs,
    results,
    simulatedCompletionAtUtc: new Date(clock.nowMs() + durationMs).toISOString(),
  };
}

export function probeRunner(runnerName, version, clock) {
  return {
    runnerName,
    version,
    help: `${runnerName} --version\n${runnerName} exec --help\n  --sandbox <mode>   one of: read-only, workspace-write\n  --cwd <dir>        working directory\n  --json             structured output\n`,
    probedAtUtc: clock.now().toISOString(),
  };
}
