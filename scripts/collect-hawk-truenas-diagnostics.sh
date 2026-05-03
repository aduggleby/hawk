#!/usr/bin/env bash

set -euo pipefail

WEB_DEFAULT="ix-hawk-hawk-1"
DB_DEFAULT="ix-sqlserver-sqlserver-1"
WEB="${WEB_CONTAINER:-$WEB_DEFAULT}"
DB="${DB_CONTAINER:-$DB_DEFAULT}"
SINCE="${SINCE:-72h}"
OUT="${OUT:-hawk-diagnostics-$(date +%Y%m%d-%H%M%S).log}"

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

read -r -s -p "SQL sa password: " SA_PASSWORD
echo

SQLCMD_PATH=""
if sudo docker exec "$DB" test -x /opt/mssql-tools18/bin/sqlcmd; then
  SQLCMD_PATH="/opt/mssql-tools18/bin/sqlcmd"
elif sudo docker exec "$DB" test -x /opt/mssql-tools/bin/sqlcmd; then
  SQLCMD_PATH="/opt/mssql-tools/bin/sqlcmd"
else
  echo "Could not find sqlcmd in container $DB." >&2
  exit 1
fi

run_section() {
  local title="$1"
  shift

  {
    echo
    echo "===== $title ====="
    echo "+ $*"
    "$@"
  } >>"$OUT" 2>&1 || {
    {
      echo
      echo "----- $title FAILED -----"
      echo "exit_code=$?"
    } >>"$OUT"
  }
}

run_sql() {
  local db_name="$1"
  local query="$2"

  if [[ -n "$db_name" ]]; then
    sudo docker exec "$DB" "$SQLCMD_PATH" -S localhost -U sa -P "$SA_PASSWORD" -C -d "$db_name" -Q "$query"
  else
    sudo docker exec "$DB" "$SQLCMD_PATH" -S localhost -U sa -P "$SA_PASSWORD" -C -Q "$query"
  fi
}

{
  echo "Hawk diagnostics started at $(date -Is)"
  echo "WEB=$WEB"
  echo "DB=$DB"
  echo "SINCE=$SINCE"
  echo "HOST=$(hostname)"
} >"$OUT"

run_section "docker ps" sudo docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'
run_section "docker ps -a" sudo docker ps -a --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}'

run_section "hawk logs recent matches" bash -lc \
  "sudo docker logs --since '$SINCE' '$WEB' 2>&1 | $SEARCH_TOOL 'Connection Timeout Expired|RecurringJobScheduler|DelayedJobScheduler|heartbeat|Worker recovered|Failed state|SqlException|Hangfire' || true"

run_section "hawk logs historical matches" bash -lc \
  "sudo docker logs '$WEB' 2>&1 | $SEARCH_TOOL '2026-02-19|2026-03-01|Connection Timeout Expired|Worker recovered|heartbeat|SqlException|Hangfire' || true"

run_section "hawk env" bash -lc \
  "sudo docker exec '$WEB' printenv | sort | $SEARCH_TOOL 'ASPNETCORE_ENVIRONMENT|ConnectionStrings|Hawk__Scheduler|Hawk__Alerting|Hawk__Resend|SA_PASSWORD' || true"

run_section "name resolution sqlserver alias" sudo docker exec "$WEB" getent hosts sqlserver
run_section "name resolution db container name" sudo docker exec "$WEB" getent hosts "$DB"
run_section "netcat presence" sudo docker exec "$WEB" sh -lc 'which nc || which netcat || true'
run_section "tcp reachability to sqlserver" sudo docker exec "$WEB" sh -lc 'nc -zv sqlserver 1433 || nc -zv '"$DB"' 1433'

run_section "sqlserver logs tail" bash -lc \
  "sudo docker logs --since '$SINCE' '$DB' 2>&1 | tail -200"
run_section "sqlserver log matches" bash -lc \
  "sudo docker logs '$DB' 2>&1 | $SEARCH_TOOL 'error|fail|timeout|memory|killed|deadlock|recovery|stack|dump|I/O' || true"

run_section "docker stats" sudo docker stats --no-stream "$WEB" "$DB"
run_section "db limits" sudo docker inspect "$DB" --format '{{.HostConfig.Memory}} {{.HostConfig.NanoCpus}}'
run_section "web limits" sudo docker inspect "$WEB" --format '{{.HostConfig.Memory}} {{.HostConfig.NanoCpus}}'
run_section "free -h" free -h
run_section "uptime" uptime
run_section "df -h" df -h

run_section "db env password marker" bash -lc \
  "sudo docker exec '$DB' printenv | grep -E '^SA_PASSWORD=' | sed 's/=.*$/=<redacted>/' || true"

run_section "sql version" run_sql "" "SELECT @@VERSION"
run_section "sql time" run_sql "" "SELECT GETUTCDATE()"

run_section "sql sessions" run_sql "" "
SELECT session_id,status,login_name,host_name,program_name,last_request_start_time,last_request_end_time
FROM sys.dm_exec_sessions
WHERE is_user_process = 1
ORDER BY last_request_start_time DESC"

run_section "sql requests" run_sql "" "
SELECT r.session_id,r.status,r.command,r.wait_type,r.wait_time,r.blocking_session_id,r.cpu_time,r.total_elapsed_time,
DB_NAME(r.database_id) AS dbname,t.text
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
ORDER BY r.total_elapsed_time DESC"

run_section "hangfire servers" run_sql "Hawk" "
SELECT * FROM HangFire.Server ORDER BY LastHeartbeat DESC"

run_section "hangfire job states" run_sql "Hawk" "
SELECT StateName, COUNT(*) AS Count
FROM HangFire.Job j
LEFT JOIN HangFire.State s ON j.StateId = s.Id
GROUP BY StateName
ORDER BY Count DESC"

run_section "hangfire queue" run_sql "Hawk" "
SELECT TOP 20 Id, Queue, FetchedAt
FROM HangFire.JobQueue
ORDER BY Id DESC"

run_section "monitors" run_sql "Hawk" "
SELECT TOP 50 Id, Name, Enabled, IsPaused, LastRunAt, NextRunAt, IntervalSeconds
FROM Monitors
ORDER BY LastRunAt DESC"

run_section "monitor runs" run_sql "Hawk" "
SELECT TOP 100 MonitorId, StartedAt, FinishedAt, State, Success, AlertSent, ErrorMessage
FROM MonitorRuns
ORDER BY StartedAt DESC"

run_section "monitor alert states" run_sql "Hawk" "
SELECT TOP 50 MonitorId, ConsecutiveFailures, FailureIncidentOpenedAt, LastFailureAlertSentAt, PendingRecoveryAlert
FROM MonitorAlertStates
ORDER BY LastFailureAlertSentAt DESC"

{
  echo
  echo "Diagnostics complete at $(date -Is)"
  echo "Output written to $OUT"
} >>"$OUT"

echo "Wrote diagnostics to $OUT"
