#!/usr/bin/env bash

set -euo pipefail

DB="${DB_CONTAINER:-ix-sqlserver-sqlserver-1}"
OUT="${OUT:-hawk-sql-log-probe-$(date +%Y%m%d-%H%M%S).log}"

{
  echo "Started at $(date -Is)"
  echo "DB=$DB"
  echo "HOST=$(hostname)"
} >"$OUT"

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

section "container status" \
  sudo docker inspect "$DB" --format '{{.State.Status}} {{.State.StartedAt}} {{.RestartCount}}'

section "errorlog grep hawk/recovery/io/login" \
  sudo docker exec "$DB" sh -lc "grep -En 'Hawk|recovery|Recovery|I/O|823|824|825|suspect|offline|Login failed|Failed to open the explicitly specified database|Error: 17|Error: 18|Error: 21|stack|dump' /var/opt/mssql/log/errorlog* || true"

section "current errorlog tail" \
  sudo docker exec "$DB" sh -lc "tail -200 /var/opt/mssql/log/errorlog"

section "all errorlog files" \
  sudo docker exec "$DB" sh -lc "ls -lah /var/opt/mssql/log/errorlog*"

section "hawk data files" \
  sudo docker exec "$DB" sh -lc "ls -lah /var/opt/mssql/data/Hawk*"

section "db mounts" \
  sudo docker exec "$DB" df -h

echo
echo "Wrote $OUT"
