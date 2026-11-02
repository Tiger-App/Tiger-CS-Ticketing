#!/usr/bin/env bash
# Builds a reproducible release package from the CURRENT clean commit.
#   tools/build-release.sh [output-dir]      (default: ./artifacts/release)
# Steps: refuse a dirty tree -> test -> EF drift check -> publish Api+Web -> idempotent migration SQL -> manifest + sha256.
# Never reads or writes secrets: appsettings in the package are the committed placeholders.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="${1:-artifacts/release}"
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then echo "Working tree has tracked changes; commit first." >&2; exit 1; fi
COMMIT=$(git rev-parse HEAD); SHORT=$(git rev-parse --short HEAD)
rm -rf "$OUT"; mkdir -p "$OUT/api" "$OUT/web"
( cd src
  dotnet test TigerCS.slnx -v q --nologo
  dotnet ef migrations has-pending-model-changes -p TigerCS.Infrastructure -s TigerCS.Api
  dotnet publish TigerCS.Api/TigerCS.Api.csproj -c Release -o "../$OUT/api" -p:SourceRevisionId="$COMMIT" --nologo -v q
  dotnet publish TigerCS.Web/TigerCS.Web.csproj -c Release -o "../$OUT/web" -p:SourceRevisionId="$COMMIT" --nologo -v q
  dotnet ef migrations script --idempotent -p TigerCS.Infrastructure -s TigerCS.Api -o "../$OUT/TigerCS-all-migrations.idempotent.sql" )
cp docs/system/13-deployment-order.md docs/system/12-configuration-reference.md "$OUT/"
( cd "$OUT" && zip -qr "TigerCS-$SHORT.zip" api web TigerCS-all-migrations.idempotent.sql 12-configuration-reference.md 13-deployment-order.md
  { echo "commit: $COMMIT"; echo "built-utc: $(date -u +%FT%TZ)"; echo "dotnet: $(dotnet --version)"; sha256sum "TigerCS-$SHORT.zip" TigerCS-all-migrations.idempotent.sql; } > MANIFEST.txt )
echo "Package: $OUT/TigerCS-$SHORT.zip"; cat "$OUT/MANIFEST.txt"
