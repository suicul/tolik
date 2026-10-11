package com.tolik.client

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView

/**
 * Task list adapter (AN-2). Buttons delegate to callbacks; the Activity runs
 * the endpoints and feeds results back into TaskTracker.
 */
class TaskAdapter(
    private val onCancel: (TaskTracker.TrackedTask) -> Unit,
    private val onConfirm: (TaskTracker.TrackedTask) -> Unit,
) : RecyclerView.Adapter<TaskAdapter.Holder>() {

    private var items: List<TaskTracker.TrackedTask> = emptyList()

    fun submit(next: List<TaskTracker.TrackedTask>) {
        items = next
        notifyDataSetChanged()
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val view = LayoutInflater.from(parent.context)
            .inflate(R.layout.item_task, parent, false)
        return Holder(view)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val task = items[position]
        val context = holder.itemView.context
        holder.description.text =
            task.description ?: context.getString(R.string.tasks_new_hint)
        holder.meta.text = buildString {
            append(context.getString(R.string.task_status_prefix))
            append(task.status ?: "Unknown")
            append("  ·  ")
            append(task.id)
            task.lastEvent?.let {
                append("\n")
                append(it)
            }
        }
        val canCancel = task.status in ACTIVE_STATUSES
        holder.cancelButton.visibility = if (canCancel) View.VISIBLE else View.GONE
        holder.cancelButton.setOnClickListener { onCancel(task) }
        holder.confirmButton.visibility = if (task.status == "Paused") View.VISIBLE else View.GONE
        holder.confirmButton.setOnClickListener { onConfirm(task) }
    }

    override fun getItemCount(): Int = items.size

    class Holder(view: View) : RecyclerView.ViewHolder(view) {
        val description: TextView = view.findViewById(R.id.task_description)
        val meta: TextView = view.findViewById(R.id.task_meta)
        val cancelButton: Button = view.findViewById(R.id.task_cancel_button)
        val confirmButton: Button = view.findViewById(R.id.task_confirm_button)
    }

    private companion object {
        // Core task states that accept cancellation (TaskLifecycle transitions).
        val ACTIVE_STATUSES = setOf("Pending", "Running", "Paused", "WaitingForConfirmation")
    }
}
