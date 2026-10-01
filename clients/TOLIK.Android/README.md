# TOLIK Android client contracts

TolikApiModels.kt contains dependency-free Kotlin data classes and a transport boundary for a thin Android client. The client supplies a Core-issued session token per request; it contains no model credentials, agent permissions, memory, or authorization policy.

An Android networking implementation can map TolikApiTransport to the app's existing HTTP stack and must preserve Core 401/403 responses as failures rather than attempting local authorization.
