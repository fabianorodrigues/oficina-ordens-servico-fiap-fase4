#!/usr/bin/env bash
set -euo pipefail

SQLCMD="/opt/mssql-tools/bin/sqlcmd"
if [ ! -x "$SQLCMD" ]; then
  SQLCMD="/opt/mssql-tools18/bin/sqlcmd"
fi

for attempt in {1..60}; do
  if "$SQLCMD" -S sqlserver,1433 -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; then
    break
  fi
  echo "Aguardando SQL Server aceitar login... tentativa ${attempt}/60"
  sleep 2
done

escape_sql_literal() {
  printf '%s' "$1" | sed -e "s/'/''/g" -e 's/[&|]/\\&/g'
}

sed -e "s|__CADASTRO_PASSWORD__|$(escape_sql_literal "$CADASTRO_DB_PASSWORD")|g" \
    -e "s|__ESTOQUE_PASSWORD__|$(escape_sql_literal "$ESTOQUE_DB_PASSWORD")|g" \
    -e "s|__ORDENS_PASSWORD__|$(escape_sql_literal "$ORDENS_DB_PASSWORD")|g" \
    -e "s|__E2E_PASSWORD__|$(escape_sql_literal "$E2E_DB_PASSWORD")|g" \
    /scripts/init.sql > /tmp/init.sql

"$SQLCMD" -S sqlserver,1433 -U sa -P "$MSSQL_SA_PASSWORD" -C -i /tmp/init.sql
