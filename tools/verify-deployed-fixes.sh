#!/usr/bin/env bash
# Read-only. Checks that a DEPLOYED build contains the Unclassified / Unassigned fixes the historical backfill depends on.
# Usage:  tools/verify-deployed-fixes.sh <deployed-commit-or-health-version>
#   <deployed-commit-or-health-version> = the "version" from  GET <prod-api>/health  (x.y.z+<commit>) or the "commit:" line of the release MANIFEST.txt.
# Exit 0 = every required fix is in the deployed build; 1 = something is missing (listed); 2 = the commit is unknown to this clone.
set -euo pipefail
raw="${1:?usage: $0 <deployed commit or /health version>}"
deployed="${raw##*+}"                       # strip "x.y.z+" if a /health version was given
git cat-file -e "${deployed}^{commit}" 2>/dev/null || { echo "Unknown commit '$deployed' (git fetch origin first, or wrong value)." >&2; exit 2; }
required=(
  "0a7d005|#158 shared CS Agent rotation, safe backfill, Genesys ownership hardening"
  "ec1a76d|#161 CS Agents see only their assigned tickets"
  "4226780|#163 final CS Agent assignment rules (Unassigned fix)"
  "b0f83f0|#164 CS Agent backfill: stale-AI option (off by default)"
  "9afe513|#169 default classification: General / General for tickets with no valid classification (Unclassified fix)"
)
missing=0
for r in "${required[@]}"; do
  c="${r%%|*}"; d="${r#*|}"
  if git merge-base --is-ancestor "$c" "$deployed"; then echo "PRESENT  $c  $d"; else echo "MISSING  $c  $d"; missing=1; fi
done
echo "deployed build: $(git log -1 --format='%h %ad %s' --date=short "$deployed")"
echo "commits on main not in the deployed build: $(git rev-list --count "$deployed"..origin/main 2>/dev/null || echo '?')"
exit $missing
