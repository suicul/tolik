import { IdentityStore } from "./identity.js";
import { PeerRegistry, SignedTransport } from "./transport.js";
import { AgentCapability, CommandStatus, MessageType, buildEnvelope } from "./contracts.js";
import { analyzeWorkspace, probeRunner, runBuild, runTests } from "./dotnet.js";
import { redactText } from "./secrets.js";
import { randomId } from "./clock.js";

export const AgentState = Object.freeze({
  Unenrolled: "Unenrolled",
  Enrolled: "Enrolled",
  Disconnected: "Disconnected",
  Quarantined: "Quarantined",
  Revoked: "Revoked",
});

export class WindowsAgent {
  constructor({ id = "win-01", name = "Windows Agent", platform = "Windows 11 26100", clock, audit, vfs }) {
    this.id = id;
    this.name = name;
    this.platform = platform;
    this.clock = clock;
    this.audit = audit;
    this.vfs = vfs;
    this.audience = `agent:${id}`;
    this.identity = new IdentityStore({ clock, audit, persistent: false });
    this.peers = new PeerRegistry();
    this.transport = null;
    this.state = AgentState.Unenrolled;
    this.enabledCapabilities = [];
    this.riskCeiling = null;
    this.enrolledAtUtc = null;
    this.installId = null;
    this.processes = [];
    this.cancelledTasks = new Set();
    this.nextPid = 4100;
    this.executions = 0;
    this.denials = 0;
  }

  async init() {
    await this.identity.init();
    this.transport = new SignedTransport({ identity: this.identity, clock: this.clock, audit: this.audit, audience: this.audience });
    this.installId = this.identity.install.installId;
    this.audit.info("Agent", "AgentStarted", `${this.name} started in state ${this.state} with tools disabled.`, {
      agentId: this.id,
      platform: this.platform,
      toolsEnabled: false,
    });
    return this;
  }

  get toolsEnabled() {
    return this.state === AgentState.Enrolled && this.enabledCapabilities.length > 0;
  }

  publicKeyJwk() {
    return this.identity.install.publicJwk;
  }

  async enroll({ corePublicJwk, coreKid, enrollmentToken, consumeEnrollmentToken, capabilities, riskCeiling }) {
    const consumed = consumeEnrollmentToken ? await consumeEnrollmentToken(enrollmentToken, this.id) : { ok: true };
    if (!consumed.ok) {
      this.audit.security("Enrollment", "AgentEnrollmentRejected", `Agent enrollment rejected: ${consumed.reason}.`, {
        agentId: this.id,
        reason: consumed.reason,
      });
      return { ok: false, reason: consumed.reason };
    }
    this.peers.pin(coreKid, { kid: coreKid, publicJwk: corePublicJwk, role: "authorization-authority", displayName: "TOLIK Core" });
    this.state = AgentState.Enrolled;
    this.enrolledAtUtc = this.clock.now().toISOString();
    this.enabledCapabilities = [...capabilities];
    this.riskCeiling = riskCeiling;
    this.audit.info("Enrollment", "AgentEnrolled", `${this.name} enrolled and received ${capabilities.length} capability grants.`, {
      agentId: this.id,
      capabilities: capabilities.join(","),
      riskCeiling,
    });
    return { ok: true, state: this.state, capabilities: this.enabledCapabilities };
  }

  setCapabilities(capabilities, riskCeiling = this.riskCeiling) {
    this.enabledCapabilities = [...capabilities];
    this.riskCeiling = riskCeiling;
    this.audit.info("Agent", "CapabilitiesUpdated", "Core updated the agent capability grants.", {
      agentId: this.id,
      capabilities: this.enabledCapabilities.join(","),
    });
    return this.enabledCapabilities;
  }

  disconnect(reason) {
    if (this.state !== AgentState.Enrolled) return this.state;
    this.state = AgentState.Disconnected;
    this.audit.warning("Agent", "AgentDisconnected", `${this.name} disconnected: ${reason}.`, { agentId: this.id, reason });
    return this.state;
  }

  quarantine(reason) {
    this.state = AgentState.Quarantined;
    this.enabledCapabilities = [];
    this.audit.security("Agent", "AgentQuarantined", `${this.name} quarantined: ${reason}.`, { agentId: this.id, reason });
    return this.state;
  }

  revoke(reason) {
    this.state = AgentState.Revoked;
    this.enabledCapabilities = [];
    this.peers = new PeerRegistry();
    this.audit.security("Agent", "AgentRevoked", `${this.name} revoked: ${reason}.`, { agentId: this.id, reason });
    return this.state;
  }

  describe() {
    return {
      agentId: this.id,
      name: this.name,
      platform: this.platform,
      state: this.state,
      toolsEnabled: this.toolsEnabled,
      enabledCapabilities: [...this.enabledCapabilities],
      riskCeiling: this.riskCeiling,
      installId: this.installId,
      enrolledAtUtc: this.enrolledAtUtc,
      keyAlgorithm: this.identity.install.algorithm,
      executions: this.executions,
      denials: this.denials,
      processes: this.processes.filter((process) => process.status === "running").length,
    };
  }

  async handleFrame(frame) {
    if (this.state === AgentState.Revoked) {
      return { ok: false, code: "AGENT_REVOKED", detail: "agent is revoked and will not process frames" };
    }
    if (this.state === AgentState.Quarantined) {
      return { ok: false, code: "AGENT_QUARANTINED", detail: "agent is quarantined" };
    }
    if (this.state !== AgentState.Enrolled) {
      this.denials += 1;
      this.audit.security("Agent", "UnenrolledExecutionDenied", "Execution denied: agent is not enrolled.", {
        agentId: this.id,
        state: this.state,
      });
      return { ok: false, code: "AGENT_NOT_ENROLLED", detail: `agent state is ${this.state}` };
    }
    const opened = await this.transport.open(frame, {
      expectedAudience: this.audience,
      resolvePublicKey: (kid) => this.peers.list().find((peer) => peer.kid === kid) ?? null,
    });
    if (!opened.ok) {
      this.denials += 1;
      return { ok: false, code: opened.code, detail: opened.detail };
    }
    const envelope = opened.envelope;
    if (envelope.messageType !== MessageType.CommandRequest) {
      return { ok: false, code: "UNSUPPORTED_MESSAGE", detail: `agent does not execute ${envelope.messageType}` };
    }
    const command = envelope.payload;
    const refExpiry = Date.parse(command.authorizationRef?.expiresAtUtc ?? "");
    if (!Number.isFinite(refExpiry) || refExpiry <= this.clock.nowMs()) {
      this.denials += 1;
      this.audit.security("Agent", "AgentDeniedExpiredAuthorization", "Execution denied: authorization reference expired.", {
        agentId: this.id,
        taskId: command.taskId,
        capability: command.capability,
      });
      return this._result(envelope, command, {
        status: CommandStatus.Denied,
        exitCode: 126,
        stdout: "",
        stderr: "authorization reference expired before execution",
        durationMs: 0,
      });
    }
    if (!this.enabledCapabilities.includes(command.capability)) {
      this.denials += 1;
      this.audit.security("Agent", "AgentDeniedCapability", `Execution denied: capability ${command.capability} is not enabled on this agent.`, {
        agentId: this.id,
        capability: command.capability,
        taskId: command.taskId,
      });
      return this._result(envelope, command, {
        status: CommandStatus.Denied,
        exitCode: 126,
        stdout: "",
        stderr: `capability ${command.capability} is not enabled on this agent`,
        durationMs: 0,
      });
    }
    if (command.arguments.taskId && this.cancelledTasks?.has(command.arguments.taskId)) {
      return this._result(envelope, command, {
        status: CommandStatus.Cancelled,
        exitCode: 130,
        stdout: "",
        stderr: "task was cancelled",
        durationMs: 0,
      });
    }
    const outcome = await this._execute(command);
    return this._result(envelope, command, outcome);
  }

  async _result(envelope, command, outcome) {
    const limits = command.limits ?? { maxOutputBytes: 65536, timeoutMs: 30000 };
    const cap = (text) => {
      const value = String(text ?? "");
      if (value.length <= limits.maxOutputBytes) return value;
      return `${value.slice(0, limits.maxOutputBytes)}\n... [output truncated at ${limits.maxOutputBytes} bytes]`;
    };
    const stdout = redactText(cap(outcome.stdout));
    const stderr = redactText(cap(outcome.stderr));
    const startedAt = this.clock.now();
    const durationMs = Math.max(0, Math.round(outcome.durationMs ?? 0));
    const resultEnvelope = buildEnvelope(
      MessageType.CommandResult,
      {
        taskId: command.taskId,
        stepId: command.stepId,
        commandCorrelationId: envelope.correlationId,
        agentId: this.id,
        capability: command.capability,
        status: outcome.status,
        exitCode: outcome.exitCode ?? 0,
        stdout: stdout.text,
        stderr: stderr.text,
        startedAtUtc: startedAt.toISOString(),
        completedAtUtc: new Date(startedAt.getTime() + durationMs).toISOString(),
        durationMs,
        dataClassification: "untrusted-tool-output",
        resultArtifact: outcome.resultArtifact ?? "",
      },
      { correlationId: envelope.correlationId },
    );
    const frame = await this.transport.seal(resultEnvelope, { audience: "core" });
    this.executions += 1;
    this.audit.info(
      "Agent",
      "ToolCompleted",
      `${command.capability} finished with status ${outcome.status}.`,
      { agentId: this.id, taskId: command.taskId, capability: command.capability, status: outcome.status, durationMs },
      { taskId: command.taskId, correlationId: envelope.correlationId },
    );
    return { ok: true, frame, result: resultEnvelope.payload };
  }

  async _execute(command) {
    const args = command.arguments ?? {};
    this.audit.info("Agent", "ToolStarted", `Executing ${command.capability}.`, {
      agentId: this.id,
      taskId: command.taskId,
      capability: command.capability,
      resource: args.path ?? args.projectPath ?? args.workingDirectory ?? "",
    });
    switch (command.capability) {
      case AgentCapability.ReadFile: {
        try {
          const content = this.vfs.readFile(args.path);
          return { status: CommandStatus.Succeeded, exitCode: 0, stdout: content, durationMs: 40 + content.length / 64 };
        } catch (error) {
          return { status: CommandStatus.Failed, exitCode: 2, stderr: error.message, durationMs: 12 };
        }
      }
      case AgentCapability.ListDirectory: {
        const entries = this.vfs.list(args.path);
        const lines = entries.map((entry) => {
          const stamp = entry.updatedAtUtc.replace("T", " ").slice(0, 16);
          const kind = entry.type === "directory" ? "<DIR>         " : String(entry.size).padStart(14);
          return `${stamp}  ${kind}  ${entry.path.split("/").pop()}`;
        });
        return { status: CommandStatus.Succeeded, exitCode: 0, stdout: lines.join("\n"), durationMs: 30 + entries.length * 4 };
      }
      case AgentCapability.WriteFile: {
        const content = args.content ?? "";
        const existing = this.vfs.stat(args.path);
        const stats = this.vfs.writeFile(args.path, content, { actor: `agent:${this.id}` });
        return {
          status: CommandStatus.Succeeded,
          exitCode: 0,
          stdout: `${existing ? "updated" : "created"} ${stats.path} (${stats.size} bytes)`,
          durationMs: 55,
        };
      }
      case AgentCapability.RunBuild: {
        const outcome = runBuild(this.vfs, { projectRoot: args.projectPath, clock: this.clock });
        return {
          status: outcome.status === "Succeeded" ? CommandStatus.Succeeded : CommandStatus.Failed,
          exitCode: outcome.exitCode,
          stdout: outcome.stdout,
          stderr: outcome.stderr,
          durationMs: outcome.durationMs,
          resultArtifact: JSON.stringify({ diagnostics: outcome.diagnostics }),
        };
      }
      case AgentCapability.RunTests: {
        const build = analyzeWorkspace(this.vfs, { projectRoot: args.projectPath });
        const outcome = runTests(this.vfs, { projectRoot: args.projectPath, clock: this.clock, buildDiagnostics: build.diagnostics });
        return {
          status: outcome.status === "Succeeded" ? CommandStatus.Succeeded : CommandStatus.Failed,
          exitCode: outcome.exitCode,
          stdout: outcome.stdout,
          stderr: outcome.stderr,
          durationMs: outcome.durationMs,
          resultArtifact: JSON.stringify({ results: outcome.results }),
        };
      }
      case AgentCapability.GetProcessStatus: {
        const running = this.processes.filter((process) => process.status === "running");
        const lines = running.map(
          (process) => `${process.pid}\t${process.name}\t${process.state}\t${process.startedAtUtc}`,
        );
        return {
          status: CommandStatus.Succeeded,
          exitCode: 0,
          stdout: lines.length > 0 ? lines.join("\n") : "no tracked child processes",
          durationMs: 18,
        };
      }
      case AgentCapability.CancelTask: {
        const target = args.taskId ?? args.targetTaskId;
        const process = this.processes.find((entry) => entry.taskId === target && entry.status === "running");
        if (!process) {
          return { status: CommandStatus.Failed, exitCode: 1, stderr: `no running process for task ${target}`, durationMs: 9 };
        }
        process.status = "cancelled";
        process.endedAtUtc = this.clock.now().toISOString();
        this.cancelledTasks = this.cancelledTasks ?? new Set();
        this.cancelledTasks.add(target);
        return {
          status: CommandStatus.Succeeded,
          exitCode: 0,
          stdout: `cancelled pid ${process.pid} (${process.name}) and reaped the child process tree`,
          durationMs: 26,
        };
      }
      case AgentCapability.RunCodex:
      case AgentCapability.RunOpenCode: {
        const runnerName = command.capability === AgentCapability.RunCodex ? "codex" : "opencode";
        const probe = probeRunner(runnerName, `${runnerName} 0.4.2`, this.clock);
        const workingDirectory = args.workingDirectory ?? "";
        const process = {
          pid: this.nextPid++,
          name: `${runnerName}.exe`,
          taskId: command.taskId,
          state: "running",
          status: "running",
          startedAtUtc: this.clock.now().toISOString(),
          endedAtUtc: null,
        };
        this.processes.push(process);
        const proposal = this._proposedPatch(workingDirectory);
        const stdout = [
          `$ ${runnerName} --version`,
          probe.version,
          `$ ${runnerName} exec --help`,
          probe.help.split("\n").slice(1).join("\n"),
          `$ ${runnerName} exec --cwd ${workingDirectory} "explain the build failure and propose the smallest fix"`,
          "considering 1 compilation error:",
          "  Kip.Parser/KipParser.cs: 'MeasurementFrame' does not contain a definition for 'ParseFrame'",
          "proposed patch:",
          proposal,
          `runner exited with code 0 after ${900 + proposal.length} ms; no further edits were applied by the runner`,
        ].join("\n");
        process.status = "completed";
        process.endedAtUtc = this.clock.now().toISOString();
        return {
          status: CommandStatus.Succeeded,
          exitCode: 0,
          stdout,
          durationMs: 900 + proposal.length,
          resultArtifact: JSON.stringify({ runner: runnerName, proposal }),
        };
      }
      default: {
        this.denials += 1;
        this.audit.security("Agent", "UnknownCapabilityDenied", "Execution denied: unknown capability. The agent has no generic shell handler.", {
          agentId: this.id,
          capability: String(command.capability),
        });
        return { status: CommandStatus.Denied, exitCode: 126, stderr: "no handler is registered for this capability", durationMs: 0 };
      }
    }
  }

  _proposedPatch(workingDirectory) {
    const source = this.vfs.stat(`${workingDirectory}/src/Kip.Parser/KipParser.cs`) ?? this.vfs.stat("C:/Work/SUEK.KIP.Suite/src/Kip.Parser/KipParser.cs");
    if (!source) return "(working tree not readable)";
    const lines = source.content.split("\n");
    const index = lines.findIndex((line) => line.includes("ParseFrame("));
    if (index === -1) return "(no change required: the tree already compiles)";
    const before = lines[index];
    const after = before.replace("MeasurementFrame.ParseFrame(", "MeasurementFrame.Parse(");
    return [
      `--- a/src/Kip.Parser/KipParser.cs`,
      `+++ b/src/Kip.Parser/KipParser.cs`,
      `@@ -${index - 1},1 +${index - 1},1 @@`,
      `-${before.trim()}`,
      `+${after.trim()}`,
    ].join("\n");
  }
}

export function describeAgent(agent) {
  return { ...agent.describe(), vfsFiles: agent.vfs.fileCount() };
}
