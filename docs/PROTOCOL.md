# Core-Agent protocol

`TOLIK.Contracts` defines the versioned, transport-neutral messages shared by Core and execution Agents. These records can be carried by HTTP, WebSocket, queues, or an in-process test transport without changing their meaning.

Every message is a `ProtocolEnvelope<TPayload>` with a string protocol version and a correlation ID. Version `v1` is the only supported version. The JSON parser requires every constructor property, rejects duplicate properties, malformed payloads, and integer enum values before a transport or execution layer receives them. Validation also rejects empty identifiers, null command arguments, unknown protocol versions, identity roles, capabilities, and risk levels.

The payloads are immutable DTOs for command requests, command results, Agent registration, and protocol events. A command identifies an allowlisted `AgentCapability`, a declared `ToolRiskLevel`, and named arguments. It deliberately contains no arbitrary shell-command field and no Windows or provider details.

`RequesterIdentity` communicates who requested work; it does not authorize that work. Agents must treat every envelope as untrusted until Core has authorized the task and the appropriate execution layer has checked capability policy. DTO risk levels describe a requested operation and never grant a permission.
