package com.tolik.client

import okhttp3.HttpUrl.Companion.toHttpUrl
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

// The /events/ws token query encoding (AN-P2). The session token is Base64 and
// CAN contain '+' and '/': a raw '+' in a query value decodes as a SPACE
// server-side, corrupting the token → the WS handshake 401s (verified live
// against the running Core). This pins the production URL builder
// [eventsWebSocketUrl] so the hazard can never silently regress.
class CephalonEventsUrlTest {

    /** A worst-case Base64 token: every hazard character (+, /) plus padding. */
    private val token = "AbCd+/12eFg+/==/"

    private val url = eventsWebSocketUrl("http://10.0.2.2:5080", token)

    @Test
    fun `token round-trips through the query parameter`() {
        assertEquals(token, url.toHttpUrl().queryParameter("token"))
    }

    @Test
    fun `hazard characters are percent-encoded, never raw`() {
        assertTrue(url.startsWith("http://10.0.2.2:5080/events/ws?token="))
        assertFalse("raw '+' would decode as a space server-side", url.substringAfter('?').contains('+'))
        assertFalse("raw '/' would be tolerated but is still escaped", url.substringAfter('?').contains('/'))
        assertEquals("token=AbCd%2B%2F12eFg%2B%2F%3D%3D%2F", url.toHttpUrl().encodedQuery)
    }

    @Test
    fun `naive raw concatenation corrupts the token - the live-verified 401`() {
        // Exactly the mistake the transport must never make: building the URL
        // by string concatenation without encoding. okhttp decodes '+' as a
        // space in query values — the same behavior Core's handshake sees.
        val naive = "http://10.0.2.2:5080/events/ws?token=$token"
        val decoded = naive.toHttpUrl().queryParameter("token")
        assertNotEquals(token, decoded)
        // Precisely how it corrupts: every '+' becomes a space server-side.
        assertEquals(token.replace('+', ' '), decoded)
    }

    @Test
    fun `plain tokens pass through unchanged`() {
        val plain = "abcdef0123456789"
        val plainUrl = eventsWebSocketUrl("http://10.0.2.2:5080", plain)
        assertEquals("token=$plain", plainUrl.toHttpUrl().encodedQuery)
        assertEquals(plain, plainUrl.toHttpUrl().queryParameter("token"))
    }

    @Test
    fun `base url trailing slash is tolerated`() {
        for (base in listOf("http://10.0.2.2:5080", "http://10.0.2.2:5080/")) {
            val built = eventsWebSocketUrl(base, token).toHttpUrl()
            assertEquals("/events/ws", built.encodedPath)
            assertEquals(token, built.queryParameter("token"))
        }
    }
}
