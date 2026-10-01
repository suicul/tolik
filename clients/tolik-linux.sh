#!/usr/bin/env bash
# TOLIK thin client for Linux (AGENTS.md §35).
# Talks to TOLIK Core over HTTP; contains no Core logic, no credentials beyond the session token.
#
# Usage:
#   export TOLIK_ENDPOINT=http://<core-host>:5080
#   export TOLIK_TOKEN=$(cat ~/.tolik/session-token)
#   tolik "разберись почему не собирается проект"
#   tolik --task "run build for project X"

set -euo pipefail

ENDPOINT="${TOLIK_ENDPOINT:-http://127.0.0.1:5080}"
TOKEN="${TOLIK_TOKEN:-}"

usage() {
  cat <<'EOF'
Usage: tolik MESSAGE [--task]
       tolik --status | --modules | --agents
       tolik --remind "TEXT" --delay SECONDS
       tolik --enroll [--code CODE]   enroll this machine as a new device
Environment: TOLIK_ENDPOINT, TOLIK_TOKEN, TOLIK_BOOTSTRAP
EOF
}

call() {
  local method="$1" path="$2" body="${3:-}"
  if [ -n "$body" ]; then
    curl -sf --max-time 120 -X "$method" "$ENDPOINT$path" \
      -H "Content-Type: application/json" \
      -H "Authorization: Bearer $TOKEN" \
      -d "$body"
  else
    curl -sf --max-time 120 -X "$method" "$ENDPOINT$path" \
      -H "Authorization: Bearer $TOKEN"
  fi
}

[ $# -eq 0 ] && { usage; exit 64; }
if [ -z "$TOKEN" ] && [ "${1:-}" != "--status" ]; then
  echo "TOLIK_TOKEN is not set — enroll first (tolik bootstrap on the Windows machine)." >&2
  exit 2
fi

case "$1" in
  --status)  call GET /status | python3 -m json.tool 2>/dev/null || call GET /status ;;
  --modules) call GET /status/modules ;;
  --agents)  call GET /agents ;;
  --help|-h) usage ;;
  --enroll)
    # New device enrollment (ECDSA P-256 challenge, mirrors TestEnrollment).
    # Needs TOLIK_BOOTSTRAP (one-time secret from the owner's .env) and openssl.
    shift
    CODE="${TOLIK_BOOTSTRAP:-}"
    while [ $# -gt 0 ]; do case "$1" in --code) CODE="$2"; shift 2 ;; *) shift ;; esac; done
    [ -n "$CODE" ] || { echo "Set TOLIK_BOOTSTRAP or pass --code (one-time secret)." >&2; exit 2; }
    command -v openssl >/dev/null || { echo "openssl not found." >&2; exit 3; }
    KEYDIR="$HOME/.tolik"; mkdir -p "$KEYDIR"; chmod 700 "$KEYDIR"
    [ -f "$KEYDIR/device.key" ] || openssl ecparam -name prime256v1 -genkey -noout -out "$KEYDIR/device.key"
    chmod 600 "$KEYDIR/device.key"
    PUBKEY=$(openssl ec -in "$KEYDIR/device.key" -pubout 2>/dev/null)
    INSTALL="linux-$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen)"
    CHALLENGE=$(curl -sf --max-time 30 -X POST "$ENDPOINT/auth/bootstrap/challenge" \
      -H "Content-Type: application/json" -d "{\"bootstrapCode\":\"$CODE\"}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["challenge"])')
    SIG=$(printf '%s' "$CHALLENGE" | openssl dgst -sha256 -sign "$KEYDIR/device.key" | base64 -w0)
    ENROLL_JSON=$(python3 -c 'import json; print(json.dumps({"bootstrapCode":sys.argv[1],"installationId":sys.argv[2],"publicKey":sys.argv[3],"challenge":sys.argv[4],"signature":sys.argv[5]}))' \
      "$CODE" "$INSTALL" "$PUBKEY" "$CHALLENGE" "$SIG")
    TOKEN_OUT=$(curl -sf --max-time 30 -X POST "$ENDPOINT/auth/bootstrap/enroll" \
      -H "Content-Type: application/json" -d "$ENROLL_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["sessionToken"])')
    printf '%s' "$TOKEN_OUT" > "$KEYDIR/session-token"; chmod 600 "$KEYDIR/session-token"
    echo "Enrolled. Token saved to $KEYDIR/session-token"
    ;;
  --remind)
    shift
    TEXT=""; DELAY=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --text)  TEXT="$2"; shift 2 ;;
        --delay) DELAY="$2"; shift 2 ;;
        *) shift ;;
      esac
    done
    call POST /proactive/remind "{\"text\":\"$TEXT\",\"delaySeconds\":$DELAY}" ;;
  --task)
    shift
    TASK_JSON=$(printf '{"description": "%s"}' "${1//\"/\\\"}")
    CREATED=$(call POST /tasks "$TASK_JSON")
    echo "$CREATED"
    ;;
  *)
    if [ "${2:-}" = "--task" ]; then
      exec "$0" --task "$1"
    fi
    MSG_JSON=$(printf '{"message": "%s"}' "${1//\"/\\\"}")
    call POST /chat "$MSG_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("answer", sys.stdin.read()))' 2>/dev/null \
      || call POST /chat "$MSG_JSON"
    ;;
esac
