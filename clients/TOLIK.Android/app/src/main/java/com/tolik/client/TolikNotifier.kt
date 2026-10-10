package com.tolik.client

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat

/**
 * System notifications for task/confirmation/proactive events (AN-2).
 * POST_NOTIFICATIONS is runtime-requested (API 33+); without the grant this
 * class is a no-op — never a silent failure of the feature, the in-app task
 * list still shows everything.
 */
object TolikNotifier {

    private const val CHANNEL_TASKS = "tolik_tasks"
    private const val CHANNEL_REMINDERS = "tolik_reminders"

    fun ensureChannels(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val tasks = NotificationChannel(
            CHANNEL_TASKS,
            context.getString(R.string.tab_tasks),
            NotificationManager.IMPORTANCE_DEFAULT,
        )
        val reminders = NotificationChannel(
            CHANNEL_REMINDERS,
            context.getString(R.string.tab_chat),
            NotificationManager.IMPORTANCE_HIGH,
        )
        manager.createNotificationChannel(tasks)
        manager.createNotificationChannel(reminders)
    }

    fun canPost(context: Context): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            ContextCompat.checkSelfPermission(
                context,
                Manifest.permission.POST_NOTIFICATIONS,
            ) == PackageManager.PERMISSION_GRANTED

    /** Runtime permission string for the request dialog. */
    val runtimePermission: String
        get() = Manifest.permission.POST_NOTIFICATIONS

    fun notifyText(context: Context, channelId: String, id: Int, title: String, text: String) {
        if (!canPost(context)) return
        val intent = Intent(context, MainActivity::class.java)
        val pending = PendingIntent.getActivity(
            context,
            0,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val notification = NotificationCompat.Builder(context, channelId)
            .setSmallIcon(android.R.drawable.ic_dialog_info)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(pending)
            .setAutoCancel(true)
            .build()
        try {
            NotificationManagerCompat.from(context).notify(id, notification)
        } catch (_: SecurityException) {
            // Permission revoked between check and notify — in-app UI still works.
        }
    }

    fun notifyTask(context: Context, id: Int, title: String, text: String) =
        notifyText(context, CHANNEL_TASKS, id, title, text)

    fun notifyReminder(context: Context, id: Int, text: String) =
        notifyText(context, CHANNEL_REMINDERS, id, context.getString(R.string.app_name), text)
}
