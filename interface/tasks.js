import { randomId } from "./clock.js";

export const TaskState = Object.freeze({
  Pending: "Pending",
  Planning: "Planning",
  WaitingForConfirmation: "WaitingForConfirmation",
  Running: "Running",
  Paused: "Paused",
  Completed: "Completed",
  Failed: "Failed",
  Cancelled: "Cancelled",
});

export const StepStatus = Object.freeze({
  Pending: "pending",
  AwaitingAuthorization: "awaiting-authorization",
  AwaitingConfirmation: "awaiting-confirmation",
  Running: "running",
  Succeeded: "succeeded",
  Failed: "failed",
  Denied: "denied",
  Cancelled: "cancelled",
});

const TRANSITIONS = {
  [TaskState.Pending]: [TaskState.Planning, TaskState.Cancelled, TaskState.Failed],
  [TaskState.Planning]: [TaskState.Running, TaskState.WaitingForConfirmation, TaskState.Failed, TaskState.Cancelled],
  [TaskState.WaitingForConfirmation]: [TaskState.Running, TaskState.Cancelled, TaskState.Failed],
  [TaskState.Running]: [
    TaskState.WaitingForConfirmation,
    TaskState.Completed,
    TaskState.Failed,
    TaskState.Cancelled,
    TaskState.Paused,
  ],
  [TaskState.Paused]: [TaskState.Running, TaskState.Cancelled],
  [TaskState.Completed]: [],
  [TaskState.Failed]: [],
  [TaskState.Cancelled]: [],
};

export class TaskManager {
  constructor({ clock, audit, origin = "user-request" }) {
    this.clock = clock;
    this.audit = audit;
    this.origin = origin;
    this._tasks = new Map();
    this._listeners = new Set();
  }

  subscribe(listener) {
    this._listeners.add(listener);
    return () => this._listeners.delete(listener);
  }

  _emit(task) {
    for (const listener of this._listeners) {
      try {
        listener(task);
      } catch {
        /* task listeners must not break the lifecycle */
      }
    }
  }

  create({ intent, text, requester, origin = this.origin, plan = null, deadline = null }) {
    const task = {
      id: randomId("task"),
      intent,
      text,
      requester,
      origin,
      status: TaskState.Pending,
      priority: "normal",
      createdAtUtc: this.clock.now().toISOString(),
      deadline,
      steps: [],
      plan,
      history: [],
      result: null,
      failure: null,
      confirmations: [],
      cancelled: false,
      autonomous: false,
    };
    this._tasks.set(task.id, task);
    this._logHistory(task, "created", `Task created from ${origin}.`);
    this.audit.info("Task", "TaskCreated", `Task created: ${text.slice(0, 120)}`, { taskId: task.id, origin }, { taskId: task.id });
    this._emit(task);
    return task;
  }

  get(taskId) {
    return this._tasks.get(taskId) ?? null;
  }

  list() {
    return [...this._tasks.values()].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
  }

  _logHistory(task, kind, detail) {
    task.history.push({ at: this.clock.now().toISOString(), kind, detail, status: task.status });
  }

  transition(taskId, nextState, detail = "") {
    const task = this._tasks.get(taskId);
    if (!task) return { ok: false, reason: "TASK_NOT_FOUND" };
    const allowed = TRANSITIONS[task.status] ?? [];
    if (!allowed.includes(nextState)) {
      this.audit.warning("Task", "IllegalTaskTransition", `Rejected task transition ${task.status} -> ${nextState}.`, {
        taskId,
        from: task.status,
        to: nextState,
      });
      return { ok: false, reason: "ILLEGAL_TRANSITION", from: task.status, to: nextState };
    }
    if (task.status === TaskState.Cancelled) return { ok: false, reason: "TASK_CANCELLED" };
    task.status = nextState;
    this._logHistory(task, "transition", `${detail || ""} -> ${nextState}`);
    this.audit.info("Task", "TaskStateChanged", `Task ${nextState}.`, { taskId, state: nextState, detail }, { taskId });
    this._emit(task);
    return { ok: true, task };
  }

  addStep(taskId, { capability, arguments: args, declaredRisk, rationale = "", requiresConfirmation = false }) {
    const task = this._tasks.get(taskId);
    if (!task) return null;
    const step = {
      id: randomId("step"),
      index: task.steps.length + 1,
      capability,
      arguments: { ...args },
      declaredRisk,
      rationale,
      requiresConfirmation,
      status: StepStatus.Pending,
      decision: null,
      decisionTrace: [],
      result: null,
      startedAtUtc: null,
      completedAtUtc: null,
    };
    task.steps.push(step);
    this._emit(task);
    return step;
  }

  updateStep(taskId, stepId, patch) {
    const task = this._tasks.get(taskId);
    if (!task) return null;
    const step = task.steps.find((entry) => entry.id === stepId);
    if (!step) return null;
    Object.assign(step, patch);
    this._emit(task);
    return step;
  }

  complete(taskId, result) {
    const task = this._tasks.get(taskId);
    if (!task) return { ok: false, reason: "TASK_NOT_FOUND" };
    task.result = result;
    return this.transition(taskId, TaskState.Completed, "result recorded");
  }

  fail(taskId, failure) {
    const task = this._tasks.get(taskId);
    if (!task) return { ok: false, reason: "TASK_NOT_FOUND" };
    task.failure = failure;
    return this.transition(taskId, TaskState.Failed, failure?.reason ?? "failed");
  }

  cancel(taskId, reason = "cancelled by requester") {
    const task = this._tasks.get(taskId);
    if (!task) return { ok: false, reason: "TASK_NOT_FOUND" };
    if ([TaskState.Completed, TaskState.Failed, TaskState.Cancelled].includes(task.status)) {
      return { ok: false, reason: "TASK_ALREADY_TERMINAL" };
    }
    task.cancelled = true;
    for (const step of task.steps) {
      if ([StepStatus.Pending, StepStatus.Running, StepStatus.AwaitingConfirmation].includes(step.status)) {
        step.status = StepStatus.Cancelled;
      }
    }
    task.status = TaskState.Cancelled;
    this._logHistory(task, "cancelled", reason);
    this.audit.warning("Task", "TaskCancelled", `Task cancelled: ${reason}.`, { taskId, reason }, { taskId });
    this._emit(task);
    return { ok: true, task };
  }

  recordConfirmation(taskId, confirmation) {
    const task = this._tasks.get(taskId);
    if (!task) return null;
    task.confirmations.push(confirmation);
    this._logHistory(task, "confirmation", `${confirmation.approved ? "approved" : "denied"} ${confirmation.code}`);
    this._emit(task);
    return confirmation;
  }
}

export function describeTask(task) {
  if (!task) return null;
  return {
    id: task.id,
    intent: task.intent,
    text: task.text,
    status: task.status,
    origin: task.origin,
    requester: task.requester,
    createdAtUtc: task.createdAtUtc,
    steps: task.steps.map((step) => ({
      id: step.id,
      index: step.index,
      capability: step.capability,
      status: step.status,
      decision: step.decision,
      rationale: step.rationale,
      resource: step.arguments?.path ?? step.arguments?.projectPath ?? step.arguments?.workingDirectory ?? null,
    })),
    result: task.result,
    failure: task.failure,
  };
}
