package com.tolik.client

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView

/** Chat transcript adapter: user / TOLIK / error bubbles (no logic inside). */
class ChatAdapter : RecyclerView.Adapter<ChatAdapter.Holder>() {

    private data class Entry(val text: String, val kind: Kind)

    private enum class Kind { USER, TOLIK, ERROR, LOADING }

    private val entries = mutableListOf<Entry>()

    fun addUser(text: String) {
        entries.add(Entry(text, Kind.USER))
        notifyItemInserted(entries.size - 1)
    }

    fun addTolik(text: String) {
        entries.add(Entry(text, Kind.TOLIK))
        notifyItemInserted(entries.size - 1)
    }

    fun addError(text: String) {
        entries.add(Entry(text, Kind.ERROR))
        notifyItemInserted(entries.size - 1)
    }

    fun addLoading() {
        entries.add(Entry("…", Kind.LOADING))
        notifyItemInserted(entries.size - 1)
    }

    fun removeLoading() {
        val index = entries.indexOfLast { it.kind == Kind.LOADING }
        if (index >= 0) {
            entries.removeAt(index)
            notifyItemRemoved(index)
        }
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder {
        val view = LayoutInflater.from(parent.context)
            .inflate(R.layout.item_chat_message, parent, false)
        return Holder(view)
    }

    override fun onBindViewHolder(holder: Holder, position: Int) {
        val entry = entries[position]
        holder.bubble.text = entry.text
        val context = holder.bubble.context
        when (entry.kind) {
            Kind.USER -> {
                holder.bubble.setBackgroundColor(context.getColor(R.color.tolik_user_bubble))
                holder.bubble.gravity = android.view.Gravity.END
            }
            Kind.TOLIK -> {
                holder.bubble.setBackgroundColor(context.getColor(R.color.tolik_tolik_bubble))
                holder.bubble.gravity = android.view.Gravity.START
            }
            Kind.ERROR -> {
                holder.bubble.setBackgroundColor(context.getColor(R.color.tolik_error_bubble))
                holder.bubble.gravity = android.view.Gravity.START
            }
            Kind.LOADING -> {
                holder.bubble.background = null
                holder.bubble.gravity = android.view.Gravity.START
            }
        }
    }

    override fun getItemCount(): Int = entries.size

    class Holder(view: View) : RecyclerView.ViewHolder(view) {
        val bubble: TextView = view.findViewById(R.id.chat_bubble)
    }
}
