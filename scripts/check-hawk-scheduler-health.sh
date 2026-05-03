#!/usr/bin/env bash

set -euo pipefail

WEB_CONTAINER="${WEB_CONTAINER:-ix-hawk-hawk-1}"
DB_CONTAINER="${DB_CONTAINER:-ix-sqlserver-sqlserver-1}"
SQL_LOGIN="${SQL_LOGIN:-hawk}"
SQL_PASSWORD="${SQL_PASSWORD:-d/6R2q2dz1Ou85FQkbaVJKeEf8IL+TmsnyBOgfRjEg0=}"
SQL_DATABASE="${SQL_DATABASE:-Hawk}"
LOG_SINCE="${LOG_SINCE:-30m}"
OUT="${OUT:-hawk-scheduler-health-$(date +%Y%m%d-%H%M%S).log}"
AUTO_RESTART="${AUTO_RESTART:-0}"

if ! command -v sudo >/dev/null 2>&1; then
  echo "sudo is required." >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required." >&2
  exit 1
fi

if command -v rg >/dev/null 2>&1; then
  SEARCH_TOOL="rg -n"
else
  SEARCH_TOOL="grep -En"
fi

SQLCMD_PATH=""
if sudo docker exec "$DB_CONTAINER" test -x /opt/mssql-tools18/bin/sqlcmd; then
  SQLCMD_PATH="/opt/mssql-tools18/bin/sqlcmd"
elif sudo docker exec "$DB_CONTAINER" test -x /opt/mssql-tools/bin/sqlcmd; then
  SQLCMD_PATH="/opt/mssql-tools/bin/sqlcmd"
else
  echo "Could not find sqlcmd in container $DB_CONTAINER." >&2
  exit 1
fi

section() {
  local title="$1"
  shift

  {
    echo
    echo "===== $title ====="
    echo "+ $*"
    "$@"
  } >>"$OUT" 2>&1 || true
}

sql_query() {
  local query="$1"

  sudo docker exec "$DB_CONTAINER" "$SQLCMD_PATH" \
    -S localhost \
    -U "$SQL_LOGIN" \
    -P "$SQL_PASSWORD" \
    -C \
    -d "$SQL_DATABASE" \
    -Q "$query"
}

{
  echo "Started at $(date -Is)"
  echo "WEB_CONTAINER=$WEB_CONTAINER"
  echo "DB_CONTAINER=$DB_CONTAINER"
  echo "SQL_LOGIN=$SQL_LOGIN"
  echo "SQL_DATABASE=$SQL_DATABASE"
  echo "LOG_SINCE=$LOG_SINCE"
  echo "AUTO_RESTART=$AUTO_RESTART"
  echo "HOST=$(hostname)"
} >"$OUT"

section "hawk connection string" \
  sudo docker exec "$WEB_CONTAINER" sh -lc "printenv | grep '^ConnectionStrings__DefaultConnection='"

section "recent hawk hangfire logs" \
  bash -lc "sudo docker logs --since '$LOG_SINCE' '$WEB_CONTAINER' 2>&1 | $SEARCH_TOOL 'Hangfire|RecurringJobScheduler|DelayedJobScheduler|Worker recovered|heartbeat|SqlException|Monitor' || true"

section "recent monitor runs" \
  sql_query "SELECT TOP 20 MonitorId, StartedAt, FinishedAt, State, Success, AlertSent, ErrorMessage FROM MonitorRuns ORDER BY StartedAt DESC"

section "recent monitor last-run state" \
  sql_query "SELECT TOP 20 Id, Name, Enabled, IsPaused, LastRunAt, NextRunAt, IntervalSeconds FROM Monitors ORDER BY LastRunAt DESC"

LATEST_RUN_RAW="$(sql_query "SET NOCOUNT ON; SELECT TOP 1 CONVERT(varchar(33), StartedAt, 126) FROM MonitorRuns ORDER BY StartedAt DESC" | tail -n 1 | tr -d '\r' | xargs || true)"

{
  echo
  echo "===== latest run timestamp ====="
  echo "$LATEST_RUN_RAW"
} >>"$OUT"

NEEDS_RESTART=0
if [[ -n "$LATEST_RUN_RAW" ]]; then
  if python3 - "$LATEST_RUN_RAW" <<'PY'
from datetime import datetime, timezone, timedelta
import sys
s = sys.argv[1]
try:
    dt = datetime.fromisoformat(s)
except ValueError:
    sys.exit(2)
if dt.tzinfo is None:
    dt = dt.replace(tzinfo=timezone.utc)
threshold = datetime.now(timezone.utc) - timedelta(hours=1)
sys.exit(0 if dt < threshold else 1)
PY
  then
    NEEDS_RESTART=1
  fi
fi

{
  echo
  echo "===== restart assessment ====="
  echo "NEEDS_RESTART=$NEEDS_RESTART"
} >>"$OUT"

if [[ "$NEEDS_RESTART" == "1" && "$AUTO_RESTART" == "1" ]]; then
  section "hawk restart" sudo docker restart "$WEB_CONTAINER"
  sleep 10
  section "post-restart hawk logs" \
    bash -lc "sudo docker logs --since 5m '$WEB_CONTAINER' 2>&1 | tail -200"
  section "post-restart monitor runs" \
    sql_query "SELECT TOP 20 MonitorId, StartedAt, FinishedAt, State, Success, AlertSent, ErrorMessage FROM MonitorRuns ORDER BY StartedAt DESC"
fi

echo
echo "Wrote $OUT"
