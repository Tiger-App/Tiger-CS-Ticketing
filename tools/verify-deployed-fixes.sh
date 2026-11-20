#!/usr/bin/env bash
# Read-only. Checks that a DEPLOYED build contains the Unclassified / Unassigned fixes the historical backfill depends on.
# Usage:  tools/verify-deployed-fixes.sh <deployed-commit-or-health-version>
#   <deployed-commit-or-health-version> = the "version" from  GET <prod-api>/health  (x.y.z+<commit>) or the "commit:" line of the release MANIFEST.txt.
#         tools/verify-deployed-fixes.sh <api-health-version> <web-health-version>   (both: the "version" from GET <api>/health and GET <web>/health)
#           -> additionally fails (exit 1) when the Api and the Web were NOT built from the same commit.
#         PR292_COMMIT=<sha of PR #292's final commit> tools/verify-deployed-fixes.sh ...   -> additionally requires PR #292 to be in the deployed build.
# Exit 0 = every required fix is in the deployed build; 1 = something is missing (listed); 2 = the commit is unknown to this clone.
set -euo pipefail
raw="${1:?usage: $0 <deployed commit or /health version>}"
deployed="${raw##*+}"                       # strip "x.y.z+" if a /health version was given
web_raw="${2:-}"
git cat-file -e "${deployed}^{commit}" 2>/dev/null || { echo "Unknown commit '$deployed' (git fetch origin first, or wrong value)." >&2; exit 2; }
required=(
  "0a7d005|#158 shared CS Agent rotation, safe backfill, Genesys ownership hardening"
  "ec1a76d|#161 CS Agents see only their assigned tickets"
  "4226780|#163 final CS Agent assignment rules (Unassigned fix)"
  "b0f83f0|#164 CS Agent backfill: stale-AI option (off by default)"
  "9afe513|#169 default classification: General / General for tickets with no valid classification (Unclassified fix)"
  "9478ad7|#288 Genesys: UTC timestamps, event-time ownership, channel wording; Closed is terminal for automated flows"
)
if [ -n "${PR292_COMMIT:-}" ]; then required+=("${PR292_COMMIT}|#292 Genesys UTC end-to-end, AI Agent ownership, no automatic Round Robin, complete Assignment History"); fi
missing=0
for r in "${required[@]}"; do
  c="${r%%|*}"; d="${r#*|}"
  if git merge-base --is-ancestor "$c" "$deployed"; then echo "PRESENT  $c  $d"; else echo "MISSING  $c  $d"; missing=1; fi
done
if [ -n "$web_raw" ]; then
  web="${web_raw##*+}"
  git cat-file -e "${web}^{commit}" 2>/dev/null || { echo "Unknown Web commit '$web'." >&2; exit 2; }
  if [ "$(git rev-parse "$deployed^{commit}")" = "$(git rev-parse "$web^{commit}")" ]; then echo "PRESENT  Api and Web were built from the same commit ($(git rev-parse --short "$deployed^{commit}"))"
  else echo "MISSING  Api ($(git rev-parse --short "$deployed^{commit}")) and Web ($(git rev-parse --short "$web^{commit}")) are NOT the same build - one of them was not redeployed"; missing=1; fi
fi
echo "deployed build: $(git log -1 --format='%h %ad %s' --date=short "$deployed")"
echo "commits on main not in the deployed build: $(git rev-list --count "$deployed"..origin/main 2>/dev/null || echo '?')"
exit $missing
