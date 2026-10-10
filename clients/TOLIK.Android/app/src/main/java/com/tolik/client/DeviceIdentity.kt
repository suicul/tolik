package com.tolik.client

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.util.UUID

/**
 * Device identity for the thin client (SECURITY §3/§11): an EC P-256 keypair
 * generated inside AndroidKeyStore — the private key is non-exportable, so a
 * copied app directory (or a stolen backup) does NOT carry usable credential
 * material. Only the public key leaves the device, and only to Core during
 * explicit enrollment.
 */
object DeviceIdentity {

    private const val KEYSTORE = "AndroidKeyStore"
    private const val KEY_ALIAS = "tolik_device_key"
    private const val PREFS = "tolik_device"
    private const val KEY_INSTALLATION = "installation_id"

    /** Base64 SPKI public key; generates the keypair on first use. */
    fun publicKeyBase64(context: Context): String {
        val keyStore = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        val existing = keyStore.getCertificate(KEY_ALIAS)?.publicKey
        if (existing == null) {
            val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, KEYSTORE)
            generator.initialize(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY,
                )
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .build(),
            )
            generator.generateKeyPair()
        }
        val publicKey = keyStore.getCertificate(KEY_ALIAS)!!.publicKey
        return Base64.encodeToString(publicKey.encoded, Base64.NO_WRAP)
    }

    /**
     * Persistent per-installation identifier (random UUID in non-backed-up
     * prefs — res/xml rules exclude tolik_device.xml as well).
     */
    fun installationId(context: Context): String {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        prefs.getString(KEY_INSTALLATION, null)?.let { return it }
        val fresh = UUID.randomUUID().toString()
        prefs.edit().putString(KEY_INSTALLATION, fresh).apply()
        return fresh
    }
}
