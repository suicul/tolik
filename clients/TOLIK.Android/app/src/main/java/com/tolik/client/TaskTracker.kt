package com.tolik.client

/**
 * Client-side task registry (AN-2). Core has NO "list tasks" endpoint — the
 * thin client tracks what it can legitimately know:
 *  - tasks it created itself (POST /tasks responses);
 *  - tasks observed on the RealtimeEventHub (per-user WS events, RealtimeEventHub.cs);
 *  - fresh status fetched per-task via GET /tasks/{id} when events arrive.
 * It holds no Core logic: statuses are echoes of Core's lifecycle events.
 */
class TaskTracker {

    data class TrackedTask(
        val id: String,
        var description: String?,
        var status: String?,
        var lastEvent: String? = null,
    )

    private val tasks = LinkedHashMap<String, TrackedTask>()
    private val listeners = mutableListOf<() -> Unit>()

    fun addListener(listener: () -> Unit) {
        listeners.add(listener)
    }

    fun snapshot(): List<TrackedTask> = synchronized(tasks) { tasks.values.toList() }

    /** A task this client created (authoritative initial state). */
    fun upsertCreated(task: TolikTask) {
        synchronized(tasks) {
            tasks[task.id] = TrackedTask(task.id, task.description, task.status ?: "Pending")
        }
        notifyListeners()
    }

    /** A task first seen via WS (description unknown until GET /tasks/{id}). */
    fun ensureTracked(taskId: String) {
        val added = synchronized(tasks) {
            if (tasks.containsKey(taskId)) false
            else {
                tasks[taskId] = TrackedTask(taskId, null, "Unknown")
                true
            }
        }
        if (added) notifyListeners()
    }

    fun applyEvent(event: TolikWsTaskEvent) {
        ensureTracked(event.taskId)
        synchronized(tasks) {
            tasks[event.taskId]?.let { task ->
                task.lastEvent = "${event.kind ?: "?"}${event.detail?.let { ": $it" } ?: ""}"
                task.status = when (event.kind) {
                    "TaskStarted", "TaskResumed" -> "Running"
                    "TaskCompleted" -> "Completed"
                    "TaskFailed" -> "Failed"
                    "TaskCancelled" -> "Cancelled"
                    "TaskProgress" -> {
                        // Pause parking-lot: detail "awaiting_confirmation:{id}"
                        if (event.detail?.startsWith("awaiting_confirmation:") == true) "Paused"
                        else task.status
                    }
                    else -> task.status
                }
            }
        }
        notifyListeners()
    }

    fun applyFetched(task: TolikTask) {
        synchronized(tasks) {
            tasks[task.id]?.let { tracked ->
                tracked.description = tracked.description ?: task.description
                tracked.status = task.status ?: tracked.status
            }
                ?: run {
                    tasks[task.id] = TrackedTask(task.id, task.description, task.status)
                }
        }
        notifyListeners()
    }

    fun needsConfirmation(taskId: String): Boolean = synchronized(tasks) {
        tasks[taskId]?.status == "Paused"
    }

    private fun notifyListeners() {
        val current = synchronized(listeners) { listeners.toList() }
        current.forEach { it() }
    }
}
