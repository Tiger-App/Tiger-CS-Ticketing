# 14. Troubleshooting guide

> Part of the [documentation set](README.md). Symptoms → cause → check → fix. Error codes are the ProblemDetails `code`/`outcome` values returned by TigerCS (see [07](07-rest-api-reference.md), [08](08-genesys-integration-guide.md)).

| Symptom | Likely cause | Check | Fix |
|---|---|---|---|
| Every `/api/genesys/*` call returns **503** | `Genesys:Enabled=false` | config | enable |
| **401** from TigerGroupWeb to Genesys | Genesys OAuth client/scope `ticketing.genesys` wrong (validated only in TigerGroupWeb) | proxy logs | fix client/scope |
| **401/403** from TigerCS to the proxy | service account not a CS Agent/Supervisor (policy `CustomerVerification`), expired JWT not renewed | `POST /api/auth/login` as the account; roles | assign CS Agent; proxy must renew+retry once on 401 |
| New route returns **404** via public URL, works direct | TigerGroupWeb forwarding missing ([15.5](15-requirements-traceability-matrix.md#155-external-handover-not-changeable-from-this-repository)) | call TigerCS directly | add forwarding |
| Ticket creation **422 `genesys-department-not-resolved`** | no queue mapping / no `departmentCode` | Admin → Genesys queue mappings; `01_Investigate_Finance_Routing_UAT.sql` | create mapping (tickets are never routed by guess) |
| Data action fails to parse response | template renders invalid JSON (fixed in 08/12 in this PR) or string input unquoted | re-import corrected JSON | use `$esc.jsonString` for strings |
| Duplicate tickets for one chat | `conversationId` differs between calls | audit `GenesysInquiryIngested` | pass the same Genesys conversation id on every call |
| Customer returns, no reopen | by design: new conversation = new ticket (D1) | — | agent reopens manually (7‑day window, reason + department) |
| Inactivity closure never happens | `BackgroundJobs:Enabled=false`, SQL scripts missing, `awaitingCustomerReply` not sent | Hangfire dashboard/tables | enable, apply scripts, add data action 10 |
| No customer e‑mail / OTP e‑mail | `EmailNotifications:Enabled=false`, SMTP creds, outbox job off | `Notifications` table, outbox | enable + creds + `BackgroundJobs` |
| OTP `503 DOCUMENT_COPY_DISABLED` | `CrmDocuments:Enabled=false` | config | enable in UAT |
| Document copy stays OFF, log error mentions `OtpCodePepper` | startup guard (host still starts; feature disabled) | env | set `CrmDocuments__OtpCodePepper` |
| Document copy stays OFF, log error mentions `AllowInProduction` | enabled in Production without sign-off (host still starts; feature disabled) | env | disable or approve deliberately |
| OTP `422 NO_EMAIL_ON_RECORD` | CRM has no usable e‑mail for the buyer | CRM record | fix CRM data |
| OTP `429` | resend too soon / limits (60 s, 3 sends, 5 challenges/h) | `Retry-After` | wait |
| OTP `423 OTP_LOCKED` | 5 wrong codes | — | start a new challenge |
| `send-copy` `403 VERIFICATION_FAILED` | session not created from OTP proof / belongs to another caller | session id source | use `otp/verify` result |
| `send-copy` `501/503 DOCUMENT_SOURCE_UNAVAILABLE` | CRM file routes not deployed/authenticating with 302/HTML | CRM | see CRM contracts; `docs/Genesys/uat/verify-crm-file-auth.sh` (read first) |
| CRM lookup `502 CRM_UNAVAILABLE` / `CRM_AUTHENTICATION_FAILED` | CRM down or wrong `Crm:SecretKey` | CRM health | fix secret/connectivity |
| Unit details all null, `NotAvailable` | CRM `GetUnitDetails` not built | — | CRM work; never substitute contract dates |
| Collections summary **404 `AccountNotFound`** | no stored PACT mapping for `customerKey` | an agent must have a ticket with that PACT customer | create ticket with the customer selected |
| Collections reads **503** | source `Unavailable`/PACTRPT down/time budget | `CollectionsSource:*`, deadlines | restore source; do not enable Fixture |
| Receivables list returns 503 for everything | one PACT row with null Amount/Date (fail‑closed by design) | PACT report | fix source row |
| Campaign overdue list empty after 1 Jan | old default window (fixed: now uses `StartDate`) | config `StartDate` | deploy this PR |
| Genesys CSV export refused | `FinancialSourceValidated=false`, date not today, or a row needs review | message | by design until Finance validates |
| Screen Pop `503 GENESYS_SCREEN_POP_NOT_CONFIGURED` | `Genesys:ScreenPopWebBaseUrl` missing | config | set URL |
| Screen Pop `403 GENESYS_AGENT_NOT_MAPPED` | agent not mapped | Admin mappings | map Genesys user → employee |
| Screen Pop customer search finds nothing | phone passed as `tel:` form (fixed) | — | deploy this PR |
| Manual ticket shows First Response **Breached** | nobody recorded the first human response | Ticket Details SLA panel | use **Record first response** (new) |
| Cannot create ticket as CS Manager | by design (CS Manager has no create) | [03](03-roles-and-permissions.md) | use CS Agent/Supervisor |
| Pending Internal step cannot be added | retired by design | — | none |
