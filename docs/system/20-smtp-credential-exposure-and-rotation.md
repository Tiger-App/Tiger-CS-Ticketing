# 20. SMTP credential exposure and rotation

> Part of the [documentation set](README.md). **The password is not reproduced anywhere in this document, in the code, in the tests or in the pull request.** Only its presence, length and location are stated.

## 20.1 What was exposed

The Microsoft 365 SMTP mailbox password used for customer e-mail (`EmailNotifications:Password`; the mailbox is the `no_reply` account named in `EmailNotifications:Username`) was committed in plain text in the settings files. It is 11 characters long and mixes lower case, upper case, digits and symbols.

| Where | When | Status after this change |
|---|---|---|
| `src/TigerCS.Api/appsettings.json` and `appsettings.Development.json` | First committed in `43c834c` (2026-10-21, "all change"). In the base settings on `main` from then until the 2026-10-23 updates; empty again at `ab7b839`. | Empty on `main` already |
| `publish/TigerCS.Api/appsettings.json` and `appsettings.Development.json` | Committed with the same change and **still present on `main` until this change** | **Removed by this change** (value set to an empty string, nothing else touched) |
| Git history | Every clone, fork and mirror of the repository that fetched any commit from `43c834c` onward, on `main` and on any branch based on it (including the branch of PR #120) | **Not changed.** History was not rewritten. |
| Release packages / `publish/` zips | `release-package.yml` has never run (no workflow runs exist), so no CI-built package carries it. A package built locally with `tools/build-release.sh` or a zip of `publish/` made from those commits would. | Cannot be seen from the repository |

**Treat the password as exposed.** Removing it from the current files does not make it safe again, because it remains in history.

## 20.2 Required action (for the mailbox owner / IT; nothing here was done automatically)

1. **Rotate the mailbox password now.** Set a new password (or the app password / modern-auth credential your Microsoft 365 policy requires) for the `no_reply` mailbox. This is a Microsoft 365 administrator action; TigerCS cannot and did not do it.
2. **Put the new secret only in a secret store**, never in a file under version control:
   * Development: `dotnet user-secrets set "EmailNotifications:Password" "<new value>"` from `src/TigerCS.Api`.
   * UAT / Production: the environment variable `EmailNotifications__Password` on the host (or your secret-store integration that feeds the same configuration key), then restart the application.
3. **Verify** after the restart: the application starts (it refuses to start with `EmailNotifications:Enabled=true` and no password, naming the key), then send one test e-mail through the normal path.
4. **Disable the old credential** only after the new one works, so mail is not interrupted.
5. **Review the mailbox for misuse** since 2026-10-21: sign-in logs, unexpected sent items and any forwarding or inbox rules created on the account.
6. **Delete copies**: any local or shared `publish/` zip or release package built from commits since `43c834c`, and check other places the repository was copied.
7. **Separate decision, not made here:** whether to purge history (`git filter-repo` plus force-push, coordinated with everyone who has a clone) or accept that the rotated, now-useless value stays in history. Rotation is what removes the risk; the purge only tidies.

If the same password was ever reused on another system, rotate it there too.

## 20.3 What this change did and did not do

Did:
* Set `EmailNotifications:Password` to an empty string in the two `publish/` settings files (byte-level edit; the diff is exactly the password length).
* Added guards to `CommittedSecretsGuardTests` that fail the build when a **non-empty** value is committed for `EmailNotifications:Password`, `Sms:Broadnet:Password`, `PactApi:ApiKey`, `Collections:GenesysOutbound:ClientSecret`, `Jwt:SigningKey` or `CrmDocuments:OtpCodePepper` in any `appsettings*.json` under `src/` **or `publish/`**. The failure message names files only, never the value. The guard was confirmed to fail on the exposed state and to pass on the fixed one.
* Added `SmtpCredentialSourceTests`, which proves the password reaches the options the SMTP transport reads **only** from a configuration source layered on top of the committed (empty) value, and that without one, enabling SMTP fails closed.

Did not:
* Rewrite Git history, delete branches, or touch any server, secret store or mailbox.
* Rotate anything (impossible from the repository).
* Remove the SMTP **username**: it is a mailbox address, not a secret, but it is account information.

## 20.4 How the application obtains the credential (verified)

`SmtpClientTransport` builds its `NetworkCredential` from `EmailNotificationOptions.Username` / `Password` (`ISmtpTransport.cs`), which are bound from the `EmailNotifications` configuration section. Configuration sources are layered, so any of the following supply the password with no code change:

| Source | Used where |
|---|---|
| `dotnet user-secrets` | Development only |
| Environment variable `EmailNotifications__Password` | UAT / Production, containers, CI |
| Any secret-store configuration provider that maps to the same key | If one is added later |
| Command-line argument | Not recommended (visible in process lists) |

The committed files carry `""`. `EmailSenderSafety.Validate` refuses to start a host that enables SMTP without a password and names the key, never the value; the password is never logged or rendered into an error.

## 20.5 Other credentials checked

Every tracked file (settings, docs, scripts, SQL, workflow files, tests, `publish/`) was scanned for private keys, cloud and token formats, JWT-shaped strings, connection-string passwords, API-key headers and secret-named JSON values. **No other live credential was found.** Items that matched and were assessed:

| Item | Assessment |
|---|---|
| `.github/workflows/db-migration-validation.yml`: `Password=` in a design-time connection string for the throw-away CI SQL Server container on `localhost` | Low risk (ephemeral container). Prefer a generated value or a repository secret; already listed in `17-release-package-and-credentials.md` 17.5. |
| `database/collections-receivables/tests/local-sqlserver/00_setup.sql`: loopback linked-server login for a local test harness | Low risk (local harness only). Confirm that password is not reused anywhere real. |
| Test fixtures (`FakeUserAccountManager`, `BroadnetSmsSenderTests`, `SmtpEmailSenderTests`) and the sample payload in `docs/design/MVP-API-Contracts.md` | Synthetic values. |
| Documentation, comments and code that mention `Password=` or `Authorization:` | Placeholders and code expressions, not values. |
| All `appsettings*.json` (`src/` and `publish/`) | After this change every secret-named value is empty, including connection-string passwords, `PactApi:ApiKey`, `Sms:Broadnet:Password` and `Collections:GenesysOutbound:ClientSecret`. |

The older exposures already recorded in `17-release-package-and-credentials.md` 17.5 (SQL passwords, `Crm:SecretKey`, `PactApi:ApiKey`) remain in history and must still be rotated if they have not been.

Limits of this check: it covers the working tree at the time of the change and obvious credential formats. It does not inspect Git history for other secrets, binaries, or places outside the repository.
