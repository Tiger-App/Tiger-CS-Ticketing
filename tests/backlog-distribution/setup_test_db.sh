#!/usr/bin/env bash
# Builds a DISPOSABLE SQL Server 2022 container for the backlog-distribution validation suite.
#  * schema  = the repo's real migration scripts (no hand-written DDL)
#  * fixture = 01_seed.sql, then a backup (/tmp/seed.bak in the container) that every test restores
# NEVER point this at a real server: it drops/creates a database named TigerCS_Ticketing_Prod inside its own container.
set -euo pipefail
C="${TIGERSQL_CONTAINER:-tigersql}"; PW="${TIGERSQL_SA_PASSWORD:-Tiger#Test2026x}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
docker rm -f "$C" >/dev/null 2>&1 || true
docker run -d --name "$C" -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$PW" -e MSSQL_PID=Developer mcr.microsoft.com/mssql/server:2022-latest >/dev/null
S=(docker exec -i "$C" /opt/mssql-tools18/bin/sqlcmd -C -I -S localhost -U sa -P "$PW" -b)   # -I = QUOTED_IDENTIFIER ON (filtered indexes)
for i in $(seq 1 60); do "${S[@]}" -Q "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done
"${S[@]}" -Q "CREATE DATABASE TigerCS_Ticketing_Prod"
for f in src/TicketingMigrations.sql AddLastCustomerInteractionToTicketInteractions.sql AddCustomerSatisfactionReviews.sql AddGenesysServiceRequestToTicketInteractions.sql AddTicketInteractionHandlers.sql AddTicketTransferTracking.sql AddTicketCreatedBy.sql AddDepartmentAssignmentRotation.sql Deploy_TigerCS_PROD_AddUserPermissionOverrides.sql tests/backlog-distribution/01_seed.sql; do
  echo "== $f"
  # the Production deploy script defaults to a dry run (rolls back); here it must really apply
  if [ "$f" = "Deploy_TigerCS_PROD_AddUserPermissionOverrides.sql" ]; then sed "s/DECLARE @DryRun bit = 1;/DECLARE @DryRun bit = 0;/" "$ROOT/$f" > /tmp/m_local.sql; docker cp /tmp/m_local.sql "$C:/tmp/m.sql"; else docker cp "$ROOT/$f" "$C:/tmp/m.sql"; fi; docker exec -u 0 "$C" chmod 644 /tmp/m.sql
  "${S[@]}" -d TigerCS_Ticketing_Prod -i /tmp/m.sql
done
"${S[@]}" -Q "BACKUP DATABASE TigerCS_Ticketing_Prod TO DISK='/tmp/seed.bak' WITH INIT, COMPRESSION"
echo "ready: python3 tests/backlog-distribution/run_tests.py   (single test: ... run_tests.py t04)"
