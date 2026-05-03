#!/usr/bin/env bash

set -euo pipefail

DB_DEFAULT="ix-sqlserver-sqlserver-1"
DB="${DB_CONTAINER:-$DB_DEFAULT}"
SQL_USER_DEFAULT="hawk"
SQL_USER="${SQL_USER:-$SQL_USER_DEFAULT}"
OUT="${OUT:-hawk-sql-investigation-$(date +%Y%m%d-%H%M%S).log}"

if ! command -v sudo >/dev/null 2>&1; then
  echo "sudo is required." >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required." >&2
  exit 1
fi

read -r -p "SQL username [$SQL_USER_DEFAULT]: " INPUT_USER
if [[ -n "${INPUT_USER}" ]]; then
  SQL_USER="${INPUT_USER}"
fi

read -r -s -p "SQL password for ${SQL_USER}: " SQL_PASSWORD
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
    local exit_code=$?
    {
      echo
      echo "----- $title FAILED -----"
      echo "exit_code=$exit_code"
    } >>"$OUT"
  }
}

run_sql() {
  local db_name="$1"
  local query="$2"

  sudo docker exec "$DB" "$SQLCMD_PATH" \
    -S localhost \
    -U "$SQL_USER" \
    -P "$SQL_PASSWORD" \
    -C \
    -d "$db_name" \
    -Q "$query"
}

{
  echo "Hawk SQL investigation started at $(date -Is)"
  echo "DB=$DB"
  echo "SQL_USER=$SQL_USER"
  echo "HOST=$(hostname)"
} >"$OUT"

run_section "db container status" sudo docker inspect "$DB" --format '{{.State.Status}} {{.State.StartedAt}} {{.RestartCount}}'
run_section "hawk container status" sudo docker inspect ix-hawk-hawk-1 --format '{{.State.Status}} {{.State.StartedAt}} {{.RestartCount}}'

run_section "database states" run_sql "master" \
  "SELECT name, state_desc, user_access_desc, recovery_model_desc FROM sys.databases ORDER BY name"

run_section "hawk database details" run_sql "master" \
  "SELECT name, state_desc, is_in_standby, is_read_only, log_reuse_wait_desc FROM sys.databases WHERE name = 'Hawk'"

run_section "errorlog hawk" run_sql "master" \
  "EXEC xp_readerrorlog 0, 1, N'Hawk'"

run_section "errorlog recovery" run_sql "master" \
  "EXEC xp_readerrorlog 0, 1, N'recovery'"

run_section "errorlog error" run_sql "master" \
  "EXEC xp_readerrorlog 0, 1, N'error'"

run_section "errorlog io" run_sql "master" \
  "EXEC xp_readerrorlog 0, 1, N'I/O'"

run_section "hawk master files" run_sql "master" \
  "SELECT DB_NAME(database_id) AS dbname, name, type_desc, physical_name, size*8/1024 AS size_mb FROM sys.master_files WHERE DB_NAME(database_id) = 'Hawk'"

run_section "db filesystem usage" sudo docker exec "$DB" df -h
run_section "db data dir" sudo docker exec "$DB" ls -lah /var/opt/mssql/data
run_section "db log dir" sudo docker exec "$DB" ls -lah /var/opt/mssql/log

run_section "host oom check" bash -lc "dmesg -T | grep -Ei 'killed process|out of memory|oom' || true"

run_section "hawk login test" run_sql "Hawk" \
  "SELECT TOP 1 GETUTCDATE() AS now_utc"

{
  echo
  echo "Investigation complete at $(date -Is)"
  echo "Output written to $OUT"
} >>"$OUT"

echo "Wrote investigation report to $OUT"
