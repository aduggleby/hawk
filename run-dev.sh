#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

DEV_DIR="$ROOT_DIR/.dev"
PID_FILE="$DEV_DIR/hawk-web.pid"
STATE_FILE="$DEV_DIR/run-dev.state"
TMUX_SESSION="hawk-dev"

SA_PASSWORD="${SA_PASSWORD:-YourStrong!Passw0rd}"
SQL_PORT="${SQL_PORT:-17833}"
WEB_PORT="${WEB_PORT:-17800}"
MOCK_PORT="${MOCK_PORT:-17801}"
START_DEPS=1
USE_TMUX=1

usage() {
  cat <<USAGE
Usage: ./run-dev.sh [options]

Runs Hawk locally for development.
Uses dotnet watch for automatic reload on file changes.

Options:
  --no-deps        Do not start Docker dependencies (db + mock).
  --tmux           Start Hawk.Web in tmux session named '${TMUX_SESSION}' and attach (default).
  --no-tmux        Run Hawk.Web in the current shell.
  -h, --help       Show this help.

Environment overrides:
  SA_PASSWORD      SQL sa password (default: YourStrong!Passw0rd)
  SQL_PORT         Local SQL port mapping (default: 17833)
  WEB_PORT         Local Hawk web port (default: 17800)
  MOCK_PORT        Local mock server port (default: 17801)
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-deps)
      START_DEPS=0
      shift
      ;;
    --tmux)
      USE_TMUX=1
      shift
      ;;
    --no-tmux)
      USE_TMUX=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 1
      ;;
  esac
done

require_cmd() {
  local cmd="$1"
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Missing required command: $cmd" >&2
    exit 1
  fi
}

require_cmd dotnet
require_cmd npm
if [[ "$START_DEPS" -eq 1 ]]; then
  require_cmd docker
fi
if [[ "$USE_TMUX" -eq 1 ]]; then
  require_cmd tmux
fi

mkdir -p "$DEV_DIR"

if [[ -f "$PID_FILE" ]]; then
  existing_pid="$(cat "$PID_FILE" 2>/dev/null || true)"
  if [[ -n "$existing_pid" ]] && kill -0 "$existing_pid" >/dev/null 2>&1; then
    echo "[run-dev] Hawk appears to already be running (pid=$existing_pid). Use ./stop-dev.sh first." >&2
    exit 1
  fi
  rm -f "$PID_FILE"
fi

if [[ "$USE_TMUX" -eq 1 ]] && tmux has-session -t "$TMUX_SESSION" 2>/dev/null; then
  echo "[run-dev] tmux session '$TMUX_SESSION' already exists. Use ./stop-dev.sh first." >&2
  exit 1
fi

echo "[run-dev] Restoring .NET packages..."
dotnet restore Hawk.sln >/dev/null

echo "[run-dev] Installing web npm dependencies..."
npm --prefix Hawk.Web install --silent

echo "[run-dev] Building Tailwind CSS..."
npm --prefix Hawk.Web run build:css >/dev/null

if [[ "$START_DEPS" -eq 1 ]]; then
  echo "[run-dev] Starting local dependencies (db + mock) with docker compose..."
  SA_PASSWORD="$SA_PASSWORD" docker compose up -d db mock >/dev/null

  echo "[run-dev] Waiting for SQL Server to be healthy..."
  db_container_id="$(docker compose ps -q db)"
  if [[ -z "$db_container_id" ]]; then
    echo "[run-dev] Could not find db container id after startup." >&2
    exit 1
  fi

  final_status=""
  for _ in {1..60}; do
    final_status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$db_container_id" 2>/dev/null || true)"
    if [[ "$final_status" == "healthy" ]]; then
      break
    fi
    sleep 2
  done

  if [[ "$final_status" != "healthy" ]]; then
    echo "[run-dev] SQL Server did not become healthy in time. Check: docker compose logs db" >&2
    exit 1
  fi
fi

cat > "$STATE_FILE" <<STATE
START_DEPS=$START_DEPS
USE_TMUX=$USE_TMUX
SQL_PORT=$SQL_PORT
WEB_PORT=$WEB_PORT
MOCK_PORT=$MOCK_PORT
STATE

run_web_cmd=(dotnet watch --project Hawk.Web run --no-launch-profile)
run_css_cmd=(npm --prefix Hawk.Web run watch:css)

if [[ "$USE_TMUX" -eq 1 ]]; then
  tmux_web_cmd=$(cat <<CMD
cd "$ROOT_DIR"
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://localhost:${WEB_PORT}"
export Hawk__DisableHttpsRedirection=true
export DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=1
export ConnectionStrings__DefaultConnection="Server=localhost,${SQL_PORT};Database=Hawk;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=true;Encrypt=false"
export Hawk__Resend__BaseUrl="http://localhost:${MOCK_PORT}"
export Hawk__Resend__ApiKey=dev
export Hawk__Email__From="Hawk <hawk@localhost>"
exec ${run_web_cmd[*]}
CMD
)

  tmux_css_cmd=$(cat <<CMD
cd "$ROOT_DIR"
exec ${run_css_cmd[*]}
CMD
)

  tmux new-session -d -s "$TMUX_SESSION" "$tmux_web_cmd"
  tmux split-window -v -t "$TMUX_SESSION:0.0" "$tmux_css_cmd"
  tmux select-layout -t "$TMUX_SESSION:0" even-vertical

  echo "[run-dev] Started Hawk.Web in tmux session '$TMUX_SESSION'."
  echo "[run-dev] Pane 1: dotnet watch"
  echo "[run-dev] Pane 2: tailwind watch"
  echo "[run-dev] Hawk URL: http://localhost:${WEB_PORT}"
  echo "[run-dev] Attaching now. Detach with: Ctrl-b d"
  echo "[run-dev] Stop with: ./stop-dev.sh"
  exec tmux attach -t "$TMUX_SESSION"
fi

echo "[run-dev] Environment configured:"
echo "  ASPNETCORE_ENVIRONMENT=Development"
echo "  ASPNETCORE_URLS=http://localhost:${WEB_PORT}"
echo "  SQL=localhost:$SQL_PORT"
echo "  Mock=http://localhost:$MOCK_PORT"
echo

echo "[run-dev] Starting Tailwind watch..."
(
  exec "${run_css_cmd[@]}"
) &
CSS_PID=$!

echo "[run-dev] Starting Hawk.Web (dotnet watch)..."
(
  export ASPNETCORE_ENVIRONMENT=Development
  export ASPNETCORE_URLS="http://localhost:${WEB_PORT}"
  export Hawk__DisableHttpsRedirection=true
  export DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=1
  export ConnectionStrings__DefaultConnection="Server=localhost,${SQL_PORT};Database=Hawk;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=true;Encrypt=false"
  export Hawk__Resend__BaseUrl="http://localhost:${MOCK_PORT}"
  export Hawk__Resend__ApiKey=dev
  export Hawk__Email__From="Hawk <hawk@localhost>"
  exec "${run_web_cmd[@]}"
) &

WEB_PID=$!
echo "$WEB_PID" > "$PID_FILE"
echo "[run-dev] Hawk URL: http://localhost:${WEB_PORT}"

cleanup() {
  if [[ -n "${CSS_PID:-}" ]] && kill -0 "$CSS_PID" >/dev/null 2>&1; then
    kill "$CSS_PID" >/dev/null 2>&1 || true
  fi
  if kill -0 "$WEB_PID" >/dev/null 2>&1; then
    kill "$WEB_PID" >/dev/null 2>&1 || true
  fi
  rm -f "$PID_FILE"
}

trap cleanup INT TERM EXIT
wait "$WEB_PID"
