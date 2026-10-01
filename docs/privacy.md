# TOLIK Privacy

## Boundary

```text
LOCAL PRIVATE CONTEXT → Context filtering (minimum necessary) → LLM request
```

Every model call must justify each context block. Default: memories + top-5
rules + persona. Nothing else travels unless the task needs it.

## Rules

1. **Family vs owner**: `Private` memories never leave the owner's scope;
   `Shared` is household-visible by explicit choice at write time.
2. **Cloud restraint**: full prompt context goes to external LLMs today —
   no PII filter exists yet (gap C7). Until it lands: prefer local Ollama
   for personal topics; treat cloud calls as disclosures.
3. **Media**: no photo/document/media types exist yet. Do NOT load family
   photos or personal archives into memory until encrypted media scopes land
   (at-rest encryption is absent — see gap C7).
4. **Voice biometrics**: speaker embeddings are Private memory, deletable,
   never logged. Enrollment is explicit; re-enrollment replaces.
5. **Audit visibility**: Owner reads all; others read own rows only. Audit
   itself never carries secrets (redacted at write + trigger boundary).
6. **Backups**: plaintext dumps — store encrypted/off-machine, rotate.

## Before loading family archives

Encryption at rest + media scopes + cloud PII filter are mandatory
preconditions, not nice-to-haves.
