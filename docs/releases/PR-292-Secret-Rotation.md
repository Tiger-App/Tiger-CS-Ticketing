# Committed secret in the old `publish/` folder — what, where, how to rotate

**Status:** the tracked `publish/` folder is removed from the current tree by PR #292 and `/publish/` is git-ignored. **Git history is NOT rewritten** (needs separate approval), so every secret that was ever in it is still readable from history and must be treated as exposed. Values are intentionally never printed here or in any log of this work.

## Affected configuration keys (found by a key-name scan; no value was read into any report)

| Key (config path) | Where it was committed | Notes |
|---|---|---|
| `EmailNotifications:Password` (SMTP password of the notification mailbox configured in `EmailNotifications:Username`, Microsoft 365 `smtp.office365.com:587`) | `publish/TigerCS.Api/appsettings.json` and `publish/TigerCS.Api/appsettings.Development.json` | **The only non-empty secret setting found.** |

Checked and clean: `publish/TigerCS.Api/appsettings.UAT.json`, both `publish/TigerCS.Web/appsettings*.json`, and every `src/**/appsettings*.json` (secret keys there are empty; connection strings are referenced by *name* only: `CollectionsSource:PactReceivables:ConnectionStringName` / `CrmConnectionStringName`). *(An earlier note in this PR also listed a `ClientSecret`; that was wrong — the key exists but is empty.)* The `Password` of the CI database container in `ci.yml` is a throw-away value for a disposable service container, not a Production secret.

## Rotation (needs the mailbox / Microsoft 365 owner; nothing here was done by Claude)

1. **Rotate the credential at the source.** In Microsoft 365 admin → Users → the notification mailbox → reset the password (or, better, move to an app-specific / SMTP-AUTH-restricted account). Assume the old one is compromised.
2. **Check for misuse before and after:** Entra ID sign-in logs and Exchange message trace for that mailbox from the exposure window (the `publish/` commit `6b228452` and its clones) — unexpected IPs, unexpected sent mail. If the same password is used anywhere else, change those too.
3. **Put the new value where only the server can read it** — never in a file in the repo or the package:
   * IIS API site: environment variable `EmailNotifications__Password` (double underscore = `:`) on the API application pool / site (IIS Manager → site → Configuration Editor → `system.webServer/aspNetCore/environmentVariables`, or machine-level), or the organisation's secret store.
   * Leave `EmailNotifications:Enabled` as it is in Production; the committed `appsettings*.json` keep the password empty (CI guard below).
4. **Restart** the API application pool; send a test notification (or use the customer-notification test path) and confirm delivery.
5. **Retire the old credential everywhere** (any developer machine / UAT that still has it in a local file or `publish/` copy).
6. If, later, history is purged (separate approval), clone fresh everywhere afterwards; until then rotation is what actually protects you.

## Guardrails added (so it cannot recur)

* `/publish/` in `.gitignore` (build output belongs outside the repo: `tools/deploy/Publish-TigerCS.ps1`, `tools/build-release.sh`, or the manual *Release package* GitHub workflow, all of which build from the exact commit and stamp it).
* `tools/check-repo-hygiene.sh` — fails when the repo tracks `publish/`, `bin/`, `obj/`, binaries (`.dll .exe .pdb .nupkg .zip .so .dylib .br .gz`), key material (`.pfx .p12 .pem .key .snk`), `.env*` files, a non-empty secret-named setting (`password/secret/apikey/signingkey/pepper/token…`) in any tracked `appsettings*.json`, or a password embedded in a connection string. Prints paths and key names only.
* Runs in CI (first step of `build-and-test`) and as `RepositoryHygieneTests`, so it also fails locally.
