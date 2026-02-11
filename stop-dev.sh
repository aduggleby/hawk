#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

DEV_DIR="$ROOT_DIR/.dev"
PID_FILE="$DEV_DIR/hawk-web.pid"
STATE_FILE="$DEV_DIR/run-dev.state"
TMUX_SESSION="hawk-dev"

stopped_any=0

if [[ -f "$PID_FILE" ]]; then
  pid="$(cat "$PID_FILE" 2>/dev/null || true)"
  if [[ -n "$pid" ]] && kill -0 "$pid" >/dev/null 2>&1; then
    echo "[stop-dev] Stopping Hawk.Web process (pid=$pid)..."
    kill "$pid" >/dev/null 2>&1 || true
    sleep 1
    if kill -0 "$pid" >/dev/null 2>&1; then
      kill -9 "$pid" >/dev/null 2>&1 || true
    fi
    stopped_any=1
  fi
  rm -f "$PID_FILE"
fi

if command -v tmux >/dev/null 2>&1 && tmux has-session -t "$TMUX_SESSION" 2>/dev/null; then
  echo "[stop-dev] Stopping tmux session '$TMUX_SESSION'..."
  tmux kill-session -t "$TMUX_SESSION"
  stopped_any=1
fi

if command -v docker >/dev/null 2>&1; then
  if docker compose ps db mock >/dev/null 2>&1; then
    echo "[stop-dev] Stopping docker services (db + mock)..."
    docker compose stop db mock >/dev/null || true
    stopped_any=1
  fi
fi

rm -f "$STATE_FILE"

if [[ "$stopped_any" -eq 0 ]]; then
  echo "[stop-dev] No run-dev processes were detected."
else
  echo "[stop-dev] Done."
fi
