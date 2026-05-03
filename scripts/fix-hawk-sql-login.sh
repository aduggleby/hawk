#!/usr/bin/env bash

set -euo pipefail

DB_CONTAINER="${DB_CONTAINER:-ix-sqlserver-sqlserver-1}"
TARGET_LOGIN="${TARGET_LOGIN:-hawk}"
TARGET_PASSWORD="${TARGET_PASSWORD:-d/6R2q2dz1Ou85FQkbaVJKeEf8IL+TmsnyBOgfRjEg0=}"
TARGET_DATABASE="${TARGET_DATABASE:-Hawk}"

if ! command -v sudo >/dev/null 2>&1; then
  echo "sudo is required." >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required." >&2
  exit 1
fi

read -r -p "SQL admin username [sa]: " ADMIN_LOGIN
ADMIN_LOGIN="${ADMIN_LOGIN:-sa}"
read -r -s -p "SQL admin password for ${ADMIN_LOGIN}: " ADMIN_PASSWORD
echo

SQLCMD_PATH=""
if sudo docker exec "$DB_CONTAINER" test -x /opt/mssql-tools18/bin/sqlcmd; then
  SQLCMD_PATH="/opt/mssql-tools18/bin/sqlcmd"
elif sudo docker exec "$DB_CONTAINER" test -x /opt/mssql-tools/bin/sqlcmd; then
  SQLCMD_PATH="/opt/mssql-tools/bin/sqlcmd"
else
  echo "Could not find sqlcmd in container $DB_CONTAINER." >&2
  exit 1
fi

sql_admin() {
  local db_name="$1"
  local query="$2"

  sudo docker exec "$DB_CONTAINER" "$SQLCMD_PATH" \
    -S localhost \
    -U "$ADMIN_LOGIN" \
    -P "$ADMIN_PASSWORD" \
    -C \
    -d "$db_name" \
    -Q "$query"
}

sql_target() {
  local db_name="$1"
  local query="$2"

  sudo docker exec "$DB_CONTAINER" "$SQLCMD_PATH" \
    -S localhost \
    -U "$TARGET_LOGIN" \
    -P "$TARGET_PASSWORD" \
    -C \
    -d "$db_name" \
    -Q "$query"
}

escape_tsql_string() {
  printf "%s" "$1" | sed "s/'/''/g"
}

TARGET_LOGIN_ESCAPED="$(escape_tsql_string "$TARGET_LOGIN")"
TARGET_PASSWORD_ESCAPED="$(escape_tsql_string "$TARGET_PASSWORD")"
TARGET_DATABASE_ESCAPED="$(escape_tsql_string "$TARGET_DATABASE")"

echo "Checking ${TARGET_LOGIN} login against ${TARGET_DATABASE}..."
if sql_target "$TARGET_DATABASE" "SELECT TOP 1 GETUTCDATE() AS now_utc" >/dev/null 2>&1; then
  echo "Login already works. No fix applied."
  exit 0
fi

echo "Target login failed. Applying fix with admin account ${ADMIN_LOGIN}..."

sql_admin "master" "
IF DB_ID(N'${TARGET_DATABASE_ESCAPED}') IS NULL
BEGIN
    THROW 50000, 'Target database does not exist.', 1;
END;

IF SUSER_ID(N'${TARGET_LOGIN_ESCAPED}') IS NULL
BEGIN
    DECLARE @create_login nvarchar(max) =
        N'CREATE LOGIN [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'] WITH PASSWORD = ''' + N'${TARGET_PASSWORD_ESCAPED}' + N''', CHECK_POLICY = OFF;';
    EXEC (@create_login);
END
ELSE
BEGIN
    DECLARE @alter_login nvarchar(max) =
        N'ALTER LOGIN [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'] WITH PASSWORD = ''' + N'${TARGET_PASSWORD_ESCAPED}' + N''', CHECK_POLICY = OFF;';
    EXEC (@alter_login);

    DECLARE @enable_login nvarchar(max) =
        N'ALTER LOGIN [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'] ENABLE;';
    EXEC (@enable_login);
END;

DECLARE @default_db_sql nvarchar(max) =
    N'ALTER LOGIN [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'] WITH DEFAULT_DATABASE = [' + REPLACE(N'${TARGET_DATABASE_ESCAPED}', N']', N']]') + N'];';
EXEC (@default_db_sql);
"

sql_admin "$TARGET_DATABASE" "
IF USER_ID(N'${TARGET_LOGIN_ESCAPED}') IS NULL
BEGIN
    DECLARE @create_user nvarchar(max) =
        N'CREATE USER [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'] FOR LOGIN [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'];';
    EXEC (@create_user);
END
ELSE
BEGIN
    DECLARE @map_user nvarchar(max) =
        N'ALTER USER [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'] WITH LOGIN = [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'];';
    EXEC (@map_user);
END;

IF IS_ROLEMEMBER(N'db_owner', N'${TARGET_LOGIN_ESCAPED}') <> 1
BEGIN
    DECLARE @role_sql nvarchar(max) =
        N'ALTER ROLE [db_owner] ADD MEMBER [' + REPLACE(N'${TARGET_LOGIN_ESCAPED}', N']', N']]') + N'];';
    EXEC (@role_sql);
END;
"

echo "Verifying repaired login..."
sql_target "$TARGET_DATABASE" "SELECT TOP 1 GETUTCDATE() AS now_utc"

echo
echo "Login repair completed for ${TARGET_LOGIN} on ${TARGET_DATABASE}."
