#!/usr/bin/env bash
# Fails when the repository tracks build output, binaries or secrets. Read-only; prints PATHS and KEY NAMES only, never a secret value.
# Run from anywhere:  tools/check-repo-hygiene.sh        (CI runs it on every push; TigerCS.Tests.RepositoryHygieneTests runs the same rules)
set -uo pipefail
cd "$(git rev-parse --show-toplevel)"
fail=0

# 1. Build output and binaries must never be tracked (publish with tools/deploy/Publish-TigerCS.ps1 / tools/build-release.sh, output outside the repo).
bad=$(git ls-files | grep -Ei '(^|/)(publish|bin|obj)/|\.(dll|exe|pdb|nupkg|zip|so|dylib|br|gz|pfx|p12|pem|key|snk)$' || true)
if [ -n "$bad" ]; then echo "FAIL: tracked build output / binaries / key material:"; echo "$bad" | head -30; fail=1; fi

# 2. No environment files, and no non-empty secret setting in any tracked appsettings*.json (secrets come from the server's environment / secret store).
python3 - <<'PY' || fail=1
import json, re, subprocess, sys
files = [f for f in subprocess.check_output(['git', 'ls-files']).decode().splitlines()
         if re.search(r'(^|/)appsettings[^/]*\.json$', f) or re.search(r'(^|/)\.env[^/]*$', f)]
sens = re.compile(r'(password|secret|apikey|signingkey|pepper|token|privatekey|accesskey|sharedaccesskey)$', re.I)
conn = re.compile(r'(password|pwd)\s*=\s*[^;"\s]+', re.I)
bad = []
def walk(o, p=''):
    if isinstance(o, dict):
        for k, v in o.items(): yield from walk(v, p + ('.' if p else '') + k)
    elif isinstance(o, list):
        for i, v in enumerate(o): yield from walk(v, f'{p}[{i}]')
    else: yield p, o
for f in files:
    if re.search(r'(^|/)\.env', f): bad.append(f'{f}: environment file is tracked'); continue
    try: text = re.sub(r'^\s*//.*$', '', open(f, encoding='utf-8-sig').read(), flags=re.M); doc = json.loads(text)
    except Exception: bad.append(f'{f}: unparseable (cannot prove it holds no secret)'); continue
    for key, value in walk(doc):
        if not isinstance(value, str) or not value.strip(): continue
        if sens.search(key.split('.')[-1]): bad.append(f'{f}: non-empty secret setting "{key}"')
        elif conn.search(value): bad.append(f'{f}: "{key}" embeds a password in a connection string')
if bad:
    print('FAIL: committed secrets (key names only; values are never printed):'); print('\n'.join(bad)); sys.exit(1)
PY
[ $fail -eq 0 ] && echo "OK: no tracked build output, binaries, key material or non-empty secret settings."
exit $fail
