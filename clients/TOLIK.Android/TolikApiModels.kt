package com.tolik.client

data class TolikStatus(val status: String?, val role: String?, val userId: String?, val deviceId: String?)
data class TolikChatRequest(val message: String)
data class TolikChatResponse(val response: String?, val message: String?, val status: String?)
data class TolikTaskRequest(val description: String)
data class TolikTask(val id: String, val description: String?, val status: String?)
data class TolikConfirmation(val confirmed: Boolean, val status: String?, val error: String?)
data class TolikApiError(val statusCode: Int, val error: String?)

interface TolikApiTransport {
    suspend fun getStatus(sessionToken: String?): Result<TolikStatus>
    suspend fun chat(request: TolikChatRequest, sessionToken: String?): Result<TolikChatResponse>
    suspend fun createTask(request: TolikTaskRequest, sessionToken: String?): Result<TolikTask>
    suspend fun getTask(taskId: String, sessionToken: String?): Result<TolikTask>
    suspend fun cancelTask(taskId: String, sessionToken: String?): Result<TolikTask>
    suspend fun confirmTask(taskId: String, sessionToken: String?): Result<TolikConfirmation>
}
