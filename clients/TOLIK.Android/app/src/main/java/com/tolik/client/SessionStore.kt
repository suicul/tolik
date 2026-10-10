package com.tolik.client

import android.content.Context
import android.content.SharedPreferences

/**
 * Device-local storage for the Core base URL and the Core-issued session token.
 *
 * Security: the token is a Core-issued session credential held only in the
 * app's private prefs (MODE_PRIVATE) and explicitly excluded from cloud
 * backup / device transfer (res/xml/*_rules.xml) — copying app data must not
 * carry usable credentials elsewhere (SECURITY §6). No other secrets live on
 * the client: no model keys, no agent credentials, no authorization policy.
 */
class SessionStore(context: Context) {

    private val prefs: SharedPreferences =
        context.getSharedPreferences("tolik_session", Context.MODE_PRIVATE)

    var baseUrl: String
        get() = prefs.getString(KEY_BASE_URL, DEFAULT_BASE_URL) ?: DEFAULT_BASE_URL
        set(value) = prefs.edit().putString(KEY_BASE_URL, value.trim()).apply()

    var sessionToken: String?
        get() = prefs.getString(KEY_TOKEN, null)
        set(value) = prefs.edit().putString(KEY_TOKEN, value).apply()

    /** Revocation = forget the token (Core-side revocation is the authority). */
    fun clearToken() {
        prefs.edit().remove(KEY_TOKEN).apply()
    }

    private companion object {
        const val KEY_BASE_URL = "base_url"
        const val KEY_TOKEN = "session_token"
        // Dev default: Core on the same machine (AGENTS.md §28 home-network phase).
        const val DEFAULT_BASE_URL = "http://127.0.0.1:5080"
    }
}
