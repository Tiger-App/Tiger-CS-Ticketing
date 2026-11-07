> **Update: one page.** There is no separate *Review & Approve* screen any more. Selecting customers, **Approve & Send to Genesys**, the confirmation, the send status and the
> administrator email all happen on **Collections Campaigns**. `/Collections/Review` only forwards to Campaigns (and keeps `?dispatch=` links working). The review data, validation
> rules, approval record, revalidation, batching and idempotency described below are unchanged; they now run behind the Campaigns page. See `Campaigns-Approve-And-Send.md`.

# Collections review, approval and Genesys outbound dispatch

> **Later round:** UAT readiness, template/export findings, V2 deployment, paid-after-upload suppression, the repeated-call block and the live-dispatch gate are in [Collections-UAT-Readiness.md](Collections-UAT-Readiness.md). Where this page differs (the shared-phone acknowledgement no longer exists; dispatch needs `LiveCustomerDispatchEnabled` and `SuppressionEnabled`), that page is current.

Page: `/Collections/Review` (Web) · API: `/api/collections/review/*` · migration `AddCollectionsReviewAndGenesysDispatch`.
Nothing here uploads real customer data or activates a campaign by default: `Collections:GenesysOutbound:Enabled` is `false`, and no
credentials are committed.

## How it works

```
 PACT (read-only)        explicit job                  stored review data                 approval + job
 ───────────────  ──►  Refresh review data  ──►  CollectionsReviewRuns/Records  ──►  Confirm  ──►  Dispatch job ──► Genesys
                       (progress shown)           (filters, counts, paging in SQL)    (exact list)    (revalidate, batch, upload)
```

* **Refresh** (`POST /refresh`, Hangfire job): one read of the PACT report, validation of every unit × reminder type, bulk save, then
  publish as the *current* run. Single-flight (a second click joins the run in progress). A failed refresh keeps the previous data.
  Browsing, filtering, counting and paging **never call PACT**; they are SQL over the stored run.
* **Review** (`GET /records`): filters company, project, unit, customer name/phone, year-month, custom due range, payment status
  (default *Unpaid or partially paid*; All / Unpaid / Partially paid / Paid / Unknown), minimum remaining (default AED 100, editable) and
  optional maximum, reminder type, validation status, failed-validation reason. Counts Ready / Needs Review / Excluded / Already Sent
  follow every filter except the status choice. Each row shows source, last refresh time and reasons in plain language.
* **Select** (`POST /selection/summary`): *ticked records* (kept across pages) or *every Ready record matching the filters, on all
  pages*. Only **Ready** records can ever be selected. The summary shows the exact count, totals by currency, count per reminder type,
  the contact list (paged) and a fingerprint of the list.
* **Confirm** (`POST /dispatches`, needs the reminder-send grant): re-resolves the list; refuses if count/fingerprint differ from what was
  shown (`409 ReviewRequired`); requires an idempotency key and an acknowledgement that an active Genesys campaign may start dialling. Records sharing a phone, or one unit with several reminder types, are held back and cannot be approved. Persists the frozen list with the initiating employee and queues a job.
* **Dispatch job**: takes a lease; re-reads current balances; records paid since approval are **excluded**, any other change (amount, due date,
  contact, validation, month rollover, disabled integration) **stops the whole dispatch for review** and releases the records; then uploads one batch
  per reminder type, ≤ 1,000 contacts each, persisting every result.

### Vocabulary
`UploadedToGenesys` means the contact is in the contact list. The system never records a call or message as delivered or "contacted" from an
upload; call and message results are separate Genesys events (the existing outcome endpoint). An active campaign may call uploaded contacts at once.

## The three flags: causes found, and what changed

Diagnosed from the code, the PACT procedure review (`pact-sql/Receivables-Source-Review.md`) and the existing tests. **No real PACT data was
available in this environment**, so each cause is established by reasoning, not measured; the refresh logs how many float-noise amounts were
normalized so the first UAT run confirms or corrects it.

| Flag | Actual cause | Change |
| --- | --- | --- |
| `SourceReconciliationRequired` | Not a bug: `Collections:Campaigns:FinancialSourceValidated` is `false` because the deployed procedures have the proven defects D1–D8 (invoice fan-out, same-date double counting, float arithmetic …) and nobody has reconciled amounts against PACT accounts. | **Not cleared.** The sign-off is now bound to the procedure it covered (`ValidatedProcedureSuffix`): switching from the original procedures to V2 invalidates it. `ReconciliationReference` records the evidence. The review header states whether the source is reconciled. |
| `AmountPrecisionNeedsReview` | The check was `amount != Round(amount, 2)` on a value converted from a **float** column. A settled instalment can come out as 1E-12 (a "positive balance" with a precision flag), and float arithmetic leaves noise on others. | The reader records whether the column is floating point. Float values within AED 0.0001 of a fils amount are snapped (a remainder that snaps to 0 is a settled instalment, not a receivable). **Anything else with more than two decimals stays flagged**, is never rounded for display, carries no quoted amount (`RemainingAmount = null`) and keeps the raw source value. |
| `NoValidContact` | The phone normalizer only accepted `+…`, `00…`, `05…` (10 chars) and `971…` (12 chars), and counted an **email** as a valid contact although the campaign is voice. Rejected: `501234567` (leading zero lost), `971 0 50…`, numbers with `.` `/` `\`, `04…` landlines, `(0)`. | New `PhoneNormalizer` repairs those UAE forms, accepts valid international numbers, and **never guesses** between two different numbers in one field. A voice campaign needs a callable phone; an email alone no longer qualifies. |

## Payment classification (the remaining-amount rule)

The verified **remaining amount** PACT returns for an instalment is the source of truth, in the Receivables list, the Campaign preview, the Manual and Auto eligibility, the send and
the Genesys export alike (`PaymentStatusRules.Derive`, `CollectionsPaymentFilters.Classify`, `PactReceivableCustomersAppService.PaymentStatus`):

| Remaining amount | Status | Campaign eligibility |
|---|---|---|
| `0` | Paid (`FullyPaid` in Receivables) | not a receivable: never a record, never in an amount |
| `> 0` | Unpaid (Receivables refines to `PartiallyPaid` only when the V2 original and paid amounts are present and consistent) | eligible on this rule; every other rule still applies |
| NULL, negative, or contradicted by the source (Status `Paid` with a remainder owed; a V2 original / paid / remaining that do not add up or a remainder above the original) | Unknown: **Needs Review** (`PaymentStatusUnknown`, with `ContradictoryPaymentStatus` or `SourceAmountsInconsistent` when that is the cause) | held back, visible with the reason |

Unpaid and partly paid are no longer told apart for eligibility; the report's own `Status` text and any configured text mapping never decide the status, and neither does the due
date. Due / Overdue stay a classification of the instalment's due date against the current Dubai month (`CollectionsDueMonth`).

## Validation rules (stored reasons)

Financial reconciliation is a **separate gate of the source**, not a customer reason: `SourceGate` decides it (sign-off `FinancialSourceValidated`, the procedure, the
snapshot-consistency evidence) **per company**, the run records whether the companies it covers are reconciled (`CollectionsReviewRun.SourceReconciled`), and the send refuses the customers of a
closed company at approval (`DispatchService`), again right before the upload (revalidation, bound to the snapshot publish the balances were just read from) and in the Auto dispatcher
(`NotReconciled`; the reconciled company is dispatched on its own). A customer whose own data is sound is **Ready** on the page while the gate is closed; the page and the summary say the source is
not reconciled, once. `SourceReconciliationRequired` is kept as the code of that gate (the Genesys export's unverified-data label); it is no longer stored on records.

### PACT is the source, the snapshot its synchronized copy

PACT is authoritative; `dbo.CollectionsRcvLine` is a copy refreshed from `p4/p32AccountReceivables` (every two hours, at start-up, on Retry: a full replace per company, published atomically
only when the result is complete, of the right company and not an implausible shrink; a failed or partial attempt keeps the previous publish, so an instalment PACT no longer returns is retired
only by a successful complete refresh). A difference between the two is therefore a **synchronization to repair**, not a reason to stop Collections:

1. **The check follows the data.** After each successful refresh of a company its snapshot-consistency check runs by itself, in the same job (`Collections:Campaigns:ReconcileAfterRefresh`,
   default true; `IPostRefreshReconciliation`). Evidence is bound to the publish it compared: a company republished after its last check is `Superseded` (gate code `EvidenceSuperseded`,
   closed) until the new publish is checked. Nothing older is silently reused.
2. **One controlled refresh, then compare again.** When a check finds differences (new instalments in PACT, changed remaining amounts, moved due dates, instalments no longer returned, new or
   missing units, payments that reduce a remainder to zero), or the snapshot is older than `ReconciliationMaxSnapshotAgeMinutes`, that company is refreshed from PACT once
   (`ReconciliationRecoveryRefresh`, default true; trigger source `Reconciliation`, which never starts another check) and compared again. The exact differences of the first comparison are
   kept on the run (`Recovery.ComparisonBefore`) and logged. A difference that was only the time since the previous refresh is **Recovered** (the run passes).
3. **A persistent difference is a defect, held per unit.** Differences confirmed after a successful refresh are a synchronization or mapping defect to investigate: the run is `Failed` with
   `PersistentAfterRefresh`, the company's gate is `ReconciledWithHeldUnits`, and only the listed units are held back (`SourceMismatchUnresolved`, Needs review, stamped on their records by the
   refresh and by the revalidation). Every other unit of that company, and the other company, stay Ready and sendable. Only when the differing units could not all be listed
   (`ReconciliationMaxDifferences`) or the recovery refresh itself failed does the company stay closed (`EvidenceFailed`). Never more than one refresh per company per run; a failed refresh is
   not retried by the check (the next scheduled refresh is the retry).
4. **Receivables, the Campaign preview and Export for Genesys** keep using the latest published snapshot; freshness (`SnapshotStatusDto`) and the gate per company are shown. The export
   labels unverified data; it never blocks.
5. **Send to Genesys** uses no stale or unverified amount: a closed company is refused, a held unit is Needs review, a publish after the approval that has not been checked stops the upload
   (`ReviewRequired`), and the revalidation compares every approved record with the current snapshot. Sign-off (`FinancialSourceValidated`) and `AutoSendingEnabled` are never set by a check.

Needs review: `AmountPrecisionNeedsReview`, `NoValidContact`, `PaymentStatusUnknown`, `StaleSource`,
`MissingUnitIdentity`, `MissingCustomerIdentity`, `UnitAllocationNeedsReview`, `AmbiguousInstalments`, `DuplicateSourceRecord`,
`ConflictingContactDetails`, `ContradictoryPaymentStatus`, `CurrencyNeedsReview`. Excluded: `OutsideSchedule`, `LegalNoticeReleaseRequired`,
`LegalCaseNotApproved`. Warning (does not block): `SharedPhoneMultipleUnits`. *Already Sent* comes from the dispatch table (a live record in a
pending, uploaded or unconfirmed dispatch).

* **Payment status** comes from source fields only. The deployed report returns `Paid`/`Installment`, which cannot separate unpaid from partially
  paid, so with the deployed procedures **every open balance is `Unknown`**: visible, never sendable. The reviewed V2 procedure also returns
  `PlanAmount`; remaining = plan → Unpaid, 0 < remaining < plan → PartiallyPaid. A due date never decides it. `SourceStatusMap` can only add a
  mapping that has been verified, and can never declare a balance paid.
* **Reminder type** follows the existing approved campaign policy unchanged (`CollectionsCampaignPolicy`): Overdue day 1, Current Month day 14,
  Follow Up day 28, Legal Notice days 12/14 (previous month's balance **strictly above AED 1,500**), Legal Case day 30 (balances older than three calendar
  months **strictly above AED 20,000**). Age alone never creates a Legal Notice/Case.
* **Money** is `decimal` end to end; `AmountDue` is formatted with the invariant culture, two decimals, no thousands separator.
* **Duplicates**: identical instalment rows (same voucher, cheque, date, amount) → `DuplicateSourceRecord`, never merged; different instalments on one
  date → `AmbiguousInstalments`. Re-sending is blocked by a unique index over live dispatch items.

## Multiple units / instalments per customer

One record per **unit × reminder type**; instalments inside a unit are summed only when no date is ambiguous; balances of different units are never
merged. **Confirmed business decision: every property unit is its own Genesys contact record and receives its own call, even when several units share a
phone number** (one customer with three units on one number = three records and three calls; two customers on one number = both records kept). A phone
number is never an identifier: records are never merged, consolidated, prioritised or held back because numbers match. The unit-level protections are what
prevent genuine duplicates: the record key (company, tenant, unit id, unit code, campaign cycle), the filtered unique index over live dispatch items, the
contact-frequency limit per unit and business day, and the idempotency key of a dispatch. The only repeated-call hold left is per **unit**: a unit that
qualifies for two reminder types scheduled on the same day (`ReminderTypeOverlap`, e.g. Legal Notice and Current Month on day 14) stays held until the
business decides the priority. `SharedPhoneMultipleUnits` / `ManualSharedPhone` are no longer produced; review data stored before the decision still shows
them until it is refreshed.

**Genesys side (UAT):** the upload sends one contact per unit with the same `Phone` value; the six-column list template has no unit column, so the rows
differ only by name, amount and date. Genesys Cloud assigns each uploaded contact its own contact id, and the Collections lists were created without a
contact-id column, so repeated numbers are not rejected by the upload. Whether the campaign then *dials* each of them is decided in Genesys, not here:
check the campaign's contact-list filters, contact sort, callable-time set, and any "do not contact" or duplicate-suppression rules on the list, and the
outbound "maximum attempts per contact / per number" settings. UAT step: upload three rows for one number to the test list and confirm three contacts
(three ids) and three attempts in the campaign's contact history before enabling live dispatch.

## Genesys

* Token: `POST {LoginBaseUrl}/oauth/token`, `Authorization: Basic base64(ClientId:ClientSecret)`, `grant_type=client_credentials`, server-side only,
  cached until `expires_in − 60 s`, one shared request for concurrent callers, invalidated on `401` (one retry). Secrets/tokens are never logged or put in errors; a refused token
  request names only the OAuth `error` code (`HTTP 400, invalid_client`), nothing else from the body.
* **"The Genesys login service refused the credentials (HTTP 400)."** The login service answered the token request with 400, so no API call was made (the contact list could not be
  read; a send is refused, an Export for Genesys falls back to the documented template and is labelled). Genesys Cloud answers 400 to a *well-formed* request it cannot honour, so the
  cause is configuration, in this order of likelihood: (1) `invalid_client`: the client id / secret pair is not an OAuth client of the org that `LoginBaseUrl` belongs to. Check the **region**:
  the default is Frankfurt (`login.mypurecloud.de` / `api.mypurecloud.de`); a client created in another region's org (for example `login.mypurecloud.ie` or the Middle East region
  `login.mec1.pure.cloud` / `api.mec1.pure.cloud`) is unknown there. The two settings must name the same region as the org that owns the contact lists. (2) `invalid_client` with the
  right region: the secret was not supplied as configured (user-secrets / environment variable `Collections__GenesysOutbound__ClientSecret`, trailing newline or quotes from a copy,
  or a secret that was regenerated in Genesys), or the client is not of grant type **Client Credentials** (a Code Authorization or Implicit client cannot use this flow).
  (3) `unsupported_grant_type` / `invalid_request`: a proxy rewrote the form body. Verify from the API host with `curl -u "<client-id>:<client-secret>" -d grant_type=client_credentials
  {LoginBaseUrl}/oauth/token` and read the `error` field; the application log line "Genesys token request was refused with HTTP 400 (<code>)" now carries the same code. The
  client also needs the `outbound` permissions of the Collections lists; a missing permission shows as 403 on the API call, not as 400 on the token.
* **"The <type> contact list could not be checked in Genesys (Genesys rejected the contact list lookup (GET /api/v2/outbound/contactlists/{contactListId} at <api host>): HTTP 404 Not Found ...)".**
  The failing request is exactly that one: before anything is uploaded the send reads the destination list (`GET {ApiBaseUrl}/api/v2/outbound/contactlists/{id}`, read-only). The message names the operation, the endpoint
  (ids shown as placeholders), the API host, the HTTP status and Genesys' own error code and correlation id (never a credential, token, body or customer value); for 404 / 403 the send message adds the exact setting
  and id to correct. What each status means for this call:

  | Status | Meaning | Correct |
  | --- | --- | --- |
  | 401 / "login service refused the credentials" | token not issued or not valid in this region | `ClientId` / `ClientSecret`, `LoginBaseUrl`, `ApiBaseUrl` (same region) |
  | **404 Not Found** | the OAuth client is authenticated, but **no list with that id exists in the organization it belongs to** (wrong id, a list of another organization or region, or deleted) | `Collections:GenesysOutbound:ContactListIds:<Type>` (the message names which, with the id) |
  | 403 Forbidden | the list exists but the client's role may not read it | give the OAuth client `outbound:contactList:view` (and `outbound:contact:add` for the upload) in Genesys Admin > Roles |
  | 400 | the request itself was malformed (not expected for this call) | report with the correlation id |

  A 404 on **every** list, with a successful login, means the ids belong to another organization than the OAuth client (typically ids copied from a different org, or a test-org client with production ids); a 404 on
  **one** list means that one id is wrong. The defaults shipped in `appsettings.json` are "the lists supplied for this integration": they are only correct for the organization that owns those lists. A wrong
  region normally fails earlier (the login at the wrong region answers 400 `invalid_client`, or the API answers 401), so 404 points at the ids or the organization, not at the region, once login works.
  Nothing falls back to another list: the send stops, and no upload happens.

  **Safe verification (read-only; nothing is uploaded, no customer is contacted, no switch is changed).** On the Campaigns page, when a send stops with a Genesys message, press **Check Genesys connection** (API:
  `POST /api/collections/review/dispatches/genesys-check`, send permission). It reports, one line each: the enabled switch and that both secrets are configured (never their values); whether `LoginBaseUrl` and
  `ApiBaseUrl` name the same region (`login.<region>` / `api.<region>`; for the Middle East that is `mec1.pure.cloud`, for Frankfurt `mypurecloud.de`); that the OAuth client authenticates and **which
  organization it acts in** (`GET /api/v2/organizations/me`); and for every `ContactListIds:*` entry whether that id exists in that organization (404), is readable (403) and carries the six template columns.
  Compare the organization name and the list ids with Genesys Admin > Outbound > Contact Lists. Correct the named value in the environment's configuration (environment variable
  `Collections__GenesysOutbound__ContactListIds__FollowUp`, or the secret store), restart, and run the check again. Production settings are never changed by the application.
* Upload: `POST {ApiBaseUrl}/api/v2/outbound/contactlists/{id}/contacts`; body `[ { "contactListId": "<same id>", "data": { "Phone", "CustomerName",
  "Email Address", "ReminderType", "AmountDue", "DueDate" }, "callable": true } ]`, all data values strings, `DueDate` `yyyy-MM-dd`. One request = one list, ≤ 1,000 contacts.

| Reminder type | `ReminderType` text | Contact list id |
| --- | --- | --- |
| Current Month | Current Month | `79e5ae74-ea6e-4941-b76d-45ddf487d8d1` |
| Follow Up | Follow Up | `3a91c06e-47ab-4a5e-a720-004bd5cf5bba` |
| Legal Case | Legal Case | `41178d2a-af65-45ae-9a33-b50260dc1b9b` |
| Legal Notice | Legal Notice | `372d81d7-6d2d-4b9e-8f09-cf2262aecfdf` |
| Overdue | Overdue | `d5f4d808-2e12-410e-8fe6-b810cca1ef4c` |

* **Outcomes**: accepted (contact ids stored); *rejected* (4xx, 429, auth, could not connect: certainly nothing created); *unknown* (timeout, dropped
  connection, 5xx, worker died mid-request). An unknown batch is **never resent**: its records stay blocked, and a person records what they found in
  Genesys (`ConfirmedUploaded` / `ConfirmedNotUploaded`, with a note and their identity). The six `data` fields are fixed, so there is no correlation key
  to look contacts up automatically; reconciliation is manual by design.
* Partial failure: a rejected batch fails only its records (they return to Ready after the next refresh); other batches stand.
* Duplicate prevention: idempotency key (replay), unique index on live records, atomic batch claim, execution lease, check-before-send.

## Blockers and decisions still open

1. **The attached CSV files were not in the repository or session.** Nothing was validated against them; please supply them for UAT field-by-field checks.
2. **Payment status** cannot be confirmed with the deployed PACT procedures (see above). Deploy the reviewed V2 procedures (or supply a verified status mapping) before anything can be Ready.
3. **Source reconciliation** against real PACT accounts has not been done (needs PACT access); `FinancialSourceValidated` must stay `false` until it is.
4. **Legal Notice / Legal Case to customers** conflicts with `Collections-Legal-Requirements.md` (human approver, templates, delivery evidence). Both are blocked by
   `LegalNoticeExportEnabled` and `LegalCaseDispatchApproved` (default `false`). Needs a Legal/Collections decision.
5. **Multi-unit policy** (above) and **Day-14 overlap** (Current Month + Legal Notice) need a business decision.
6. **`ReminderType` text** follows the names in the contact-list table; the supplied templates were not available to verify. Override with `ReminderTypeLabels` if they differ.
7. **Suppression after upload** (a customer pays after the contact is uploaded and before a campaign dials) is not implemented; it needs the Genesys contact update API and a decision on
   whether campaigns run continuously. Until then, keep campaigns inactive or time uploads close to the dialling window.
8. Revalidation re-reads the company's PACT window (the procedures cannot filter by customer): about a minute per company, in the background job.

## Campaign filters, approval snapshot and history

* **Campaigns page filters:** Stage, Project / Tower, Payment status (Due / Overdue / Both, default Both), From / To date, the *Last 6 months* shortcut, Minimum Total and Search.
  Month and Year were removed (an old bookmark that still carries `month` + `year` is read as that whole-month range). All filters are applied by the API before paging; the preview,
  its counts, the eligible-customer count and total qualifying amount, every page and both CSV exports (`paymentStatus` included) use the same parameters.
* **Payment status** keeps only that status' instalments in a unit's Due / Overdue / Total (Due = inside the current Dubai month, Overdue = before it), so Minimum Total and the totals follow it.
  The stage eligibility rule is not affected. A single status is always evaluated by the reference (in-memory) engine, never by a data-store engine that does not know the filter.
* **Review & approve** has a matching *Due / Overdue* filter (`dueStatus`; it narrows the due-date range and travels inside the stored filter), and the *Review & approve* link on Campaigns carries
  stage, dates and status. The confirmation shows the selected stage, the Genesys campaign name (identical to the stage name), project, status, date range, eligible customers and total. Approval
  and dispatch re-resolve that same filter (stored as the dispatch's `FilterJson`) and keep the existing fingerprint check and balance revalidation.
* **Send history** (Review page): per batch the stage name, Genesys campaign name and id, created / reused existing, and the dispatch status.

## Project, Due / Overdue, dates and amounts: exactly how they relate

**Project / Tower.** One structured filter (the tower id) on both pages. Campaigns sends it to the source; Review stores each record's tower (`TowerId`, number, name; migration
`AddTowerToReviewRecords`) and filters on it. The *Review & approve* link carries it, and so do approval (the stored filter / `FilterJson`), the frozen dispatch list, the CSV exports and the Genesys
upload: a dispatch contains only the approved project's records, and approving a list after changing the project is refused (`ReviewRequired`). Review data read before this change has no tower,
so choosing a project there lists nothing and says "refresh the review data"; it never falls back to all projects. The old free-text `project` URL parameter still works for bookmarks.

**Due and Overdue are measured against today (Dubai), not against the chosen dates.** Due = an instalment due inside the *current calendar month*; Overdue = due before the first day of
the current month; nothing due from next month on is ever listed (`CollectionsDueMonth`). The From / To dates only choose which instalments are *read*: [From, To] with To never past the end of the
current month, and an empty From meaning the whole history. So:

| Dates | Both | Due | Overdue |
| --- | --- | --- | --- |
| empty (default) | everything up to month end | current month only | before this month |
| inside the current month | current month | current month | nothing - the page says so |
| entirely before this month | those instalments (all Overdue) | nothing - the page says so | those instalments |
| spanning last month and this month | both parts | the current-month part | the part before this month |

A single status removes the other status' instalments from a unit's Due / Overdue / Total *before* the Minimum Total, the counts, the totals, paging and the exports are computed.
A combination that can match nothing shows a note (`PaymentStatusNote`) instead of an unexplained empty list.

**Stage eligibility is independent of both.** A unit is eligible for a stage from its own stage rows (`CollectionsCampaignPolicy`), and its stage amount is unchanged by the status or the
dates (an explicit From only moves where eligibility starts looking, as before). The status and the dates therefore decide which units are *listed*; the stage rule decides which of them are *eligible*.

**Review's Due / Overdue** filters the due-date range at the first day of the current month. Every stage window lies wholly on one side of it - Current Month and Follow Up are Due; Overdue,
Legal Notice and Legal Case are Overdue - so on Review the status is the stage's own class, and it agrees with Campaigns for the same stage.

**Total qualifying amount** (Campaigns) = the sum of the *stage amounts* of the eligible units over the whole filtered list (not the current page): the same figure Review & approve shows and Genesys
receives as `AmountDue`. Under Both, Due and Overdue the list (and therefore the total) follows the status; a unit's stage amount does not. The *outstanding balance* line beside it is the units'
Due + Overdue (the balance columns of the table), which is a different, larger-or-equal number by design. Units whose stage instalments are ambiguous have no stage amount and add 0.

**Known differences and limits**
* An instalment due *later in the current month* (e.g. 20 October when today is 14 October) is a stage row for Review (its window is the whole month and it reads to month end) but not for Campaigns
  (eligibility ends at today). Both rules predate this work and were left unchanged; a test pins the difference. Which behaviour is intended is a business decision.
* With a data-store campaign engine the qualifying and outstanding amounts and the eligible count are not computed (the production source uses the in-memory engine).
* Review's Company filter remains; choosing a tower already implies its company.

## Genesys campaign per stage

When `Collections:GenesysOutbound:Campaign:Enabled` is `true`, each upload batch first resolves a Genesys campaign whose **name is exactly the stage name**
(`CampaignReminderTypes.StageName`), then uploads its contacts. The stage enum stays the internal id; the eligibility rules and customer filtering are unchanged.

| Stage | Genesys campaign name |
| --- | --- |
| Overdue | `Overdue reminder - day 1` |
| Current Month | `Current month reminder - day 14` |
| Follow Up | `Follow-up reminder - day 28` |
| Legal Notice | `Legal notice - days 12 and 14` |
| Legal Case | `Internal Legal referral - day 30` |

* The same name is shown on the Campaigns stage picker and, per reminder type, in the approval summary.
* Lookup is `GET /api/v2/outbound/campaigns?name=...`. Exactly one match that dials the stage's contact list is **reused**; nothing is created.
* Several matches, a name that differs only by letter case (Genesys names are unique case-insensitively), or a match dialling another contact list **fail the batch**
  before any upload; the records are released. An unreadable or incomplete lookup also fails the batch and never leads to a create.
* With no match and `AllowCreate`, the campaign is created **off** from `Campaign:QueueId`, `DialingMode`, `CallerName`, `CallerAddress` (required) and
  `ScriptId`, `CallAnalysisResponseSetId` (optional). An unconfirmed creation uploads nothing; the next send looks the campaign up first and reuses it.
* Each batch stores `StageName`, `GenesysCampaignName`, `GenesysCampaignId` and `CampaignAction` (`Created` / `ReusedExisting`), returned on the dispatch DTO, shown in the
  batch table and written to the log (stage name and campaign id only). Migration `AddGenesysCampaignToDispatchBatch`.
* With `Campaign:Enabled=false` (default) the flow is unchanged: contacts go to the configured lists, and only `StageName` is recorded.
* Genesys field requirements for campaign creation (and that `name` filters the list) should be verified against the target org in UAT.

## Configuration

Non-secret keys are in `appsettings.json`; supply secrets by user-secrets/environment only.

| Key | Default | Notes |
| --- | --- | --- |
| `Collections:Enabled`, `CollectionsSource:PactReceivables:Enabled` | `true` / `false` | existing |
| `CollectionsSource:PactReceivables:ProcedureSuffix` | `""` | `V2` once the reviewed procedures are deployed |
| `Collections:Campaigns:FinancialSourceValidated` | `false` | only after reconciliation |
| `Collections:Campaigns:ValidatedProcedureSuffix` | `""` | procedure the reconciliation covered |
| `Collections:Campaigns:ReconciliationReference` | – | evidence reference |
| `Collections:Campaigns:LegalNoticeExportEnabled` / `LegalCaseDispatchApproved` | `false` / `false` | see blocker 4 |
| `Collections:Campaigns:ReconcileAfterRefresh` | `true` | run the company's snapshot-consistency check by itself right after each successful snapshot refresh (scheduled, start-up, Retry) |
| `Collections:Campaigns:ReconciliationRecoveryRefresh` | `true` | when a check finds differences or the snapshot is too old, refresh that company from PACT once and compare again in the same run; a persisting difference holds only its units |
| `Collections:Campaigns:ReconciliationStartDate` | `2014-01-01` | first due date of the snapshot-consistency check; the window ends at the end of the current month. Both sides (snapshot and a direct read of the same PACT procedure) are read for that window; it never reaches before the snapshot coverage (`ReceivablesSnapshot:SourceFromDate`, 2000-01-01). The Campaigns scope (`PactReceivables:StartDate`) and the Receivables filters are separate |
| `Collections:Review:DefaultMinimumRemaining` | `100` | AED |
| `Collections:Review:MaxSelection` | `20000` | one approval |
| `Collections:Review:FloatNoiseTolerance` | `0.0001` | AED |
| `Collections:GenesysOutbound:Enabled` | `false` | master switch |
| `Collections:GenesysOutbound:LoginBaseUrl` / `ApiBaseUrl` | `https://login.mypurecloud.de` / `https://api.mypurecloud.de` | region |
| `Collections:GenesysOutbound:ClientId` | *secret* | `Collections__GenesysOutbound__ClientId=<client-id-placeholder>` |
| `Collections:GenesysOutbound:ClientSecret` | *secret* | `Collections__GenesysOutbound__ClientSecret=<client-secret-placeholder>` |
| `Collections:GenesysOutbound:ContactListIds:<ReminderType>` | table above | |
| `Collections:GenesysOutbound:ReminderTypeLabels:<ReminderType>` | – | optional text override |
| `Collections:GenesysOutbound:BatchSize` | `1000` | clamped to 1–1000 |
| `BackgroundJobs:Enabled` | `true` | with `false`, refresh and dispatch jobs stay *Queued* and are shown as such (cancel a queued dispatch to release its records) |

```
dotnet user-secrets set "Collections:GenesysOutbound:ClientId" "<client-id-placeholder>"   --project src/TigerCS.Api
dotnet user-secrets set "Collections:GenesysOutbound:ClientSecret" "<client-secret-placeholder>" --project src/TigerCS.Api
```

## Database

Migration `20261009065134_AddCollectionsReviewAndGenesysDispatch` adds `CollectionsReviewRuns`, `CollectionsReviewRecords`, `CollectionsDispatches`,
`CollectionsDispatchItems`, `CollectionsGenesysBatches` (only new tables). Idempotent script for SQL Server: `AddCollectionsReviewAndGenesysDispatch.sql` in the repository root
(guarded by `__EFMigrationsHistory`, safe to run twice). Or `dotnet ef database update --project src/TigerCS.Infrastructure`.
Important constraints: `UX_CollectionsDispatchItems_LiveRecord` (filtered unique), `UX_CollectionsDispatches_IdempotencyKey`, `UX_CollectionsReviewRuns_Active/Current`.

## Authorization

Browse/refresh: Collections financial-read grant. Select, confirm, cancel, reconcile: the reminder-send grant (CS Supervisor, CS Manager, Collections department members,
configured integration accounts, System Administrator override). Authorization runs before any data is read.

## UAT

1. With the committed defaults open *Collections → Campaigns → Review & approve*. Click **Refresh review data**: progress bar; the page keeps working; a second click joins the run.
2. Check source, last-refresh time, counts. Expect everything *Needs Review* (`SourceReconciliationRequired`, `PaymentStatusUnknown`) until V2 + reconciliation are in place. Filter by each reason.
3. In UAT only: deploy V2, run the read-only reconciliation SQL, set `FinancialSourceValidated=true`, `ValidatedProcedureSuffix=V2`, `ReconciliationReference`; refresh; Ready rows appear on schedule days.
4. Filter (company, project, unit, customer name / phone fragment, month, due range, payment status, min/max, reminder type, validation status, reason); compare counts with PACT.
5. Tick records on two pages, *Review selection*: exact count, totals by currency, contacts, shared-phone warning. Try *all matching filters*.
6. In UAT with `GenesysOutbound:Enabled=true`, a **test contact list and no active campaign**: confirm a handful; verify the contacts, field values, list routing and `callable`.
7. Mark a unit paid in PACT after approval: it is excluded at dispatch; change an amount instead: the whole dispatch stops for review.
8. Double-click Confirm; open the page in two tabs and confirm the same list: one dispatch only. Simulate a Genesys timeout: batch shows *Unconfirmed*; reconcile it.
9. Confirm that no row ever says "contacted" or "delivered".

## Verification

`dotnet build src/TigerCS.slnx -c Release` → 0 warnings, 0 errors. `dotnet test` → 3,931 passed, 0 failed (152 new: domain rules, validation, filters/paging,
dispatch/authorization/duplicates/concurrency on a real SQLite database, Genesys HTTP/token behaviour against a mocked handler, page rendering, endpoint authorization).
Not exercised: real PACT, real Genesys, SQL Server (the migration and filtered indexes were generated for SQL Server; tests run on SQLite).

## UAT: PACT synchronization, reconciliation recovery and the per-company gate

Environment: UAT with `BackgroundJobs:Enabled=true`, the linked server and the direct `PACTRPT` connection configured, `Collections:GenesysOutbound:LiveCustomerDispatchEnabled=false`
(nothing is sent to a customer at any step), `AutoSendingEnabled=false`, `FinancialSourceValidated` as committed (false) for steps 1-6 and set to true (UAT only, with
`ValidatedProcedureSuffix=""` and a `ReconciliationReference`) for steps 7-10. The checks read PACT; they never write to it.

1. **The check follows the refresh.** Start the API; both companies load (`RefreshOnStartup`). Open `/Collections/Reconciliation`: within a few minutes of each company's refresh a run
   "run by itself after the snapshot refresh" appears for that company, Passed, with "snapshot published" equal to the company's last refresh time on `/Collections/Receivables`.
2. **Changed amount / paid instalment / new unit.** In UAT PACT (or by waiting for real payments) change the remaining amount of one instalment, settle another, and add one. Press *Dubai
   only*. Expected: the latest run shows the company **Passed** with the recovery line "The first comparison found N differing unit(s); the company's snapshot was refreshed from PACT once
   (Succeeded) and the second comparison agrees". Receivables shows the new amounts, the settled instalment is gone, the new unit is listed. Only one refresh attempt of that company is recorded
   in `dbo.CollectionsRcvRun` with `TriggerSource = 'Reconciliation'`.
3. **Persistent difference (mapping defect).** In the UAT database only, edit one published line in `dbo.CollectionsRcvLine` (change its Amount) AND make the UAT refresh procedure
   reproduce it (or run the check while a known linked-server/direct discrepancy exists). Press *Dubai only*. Expected: the company is **Failed**, the recovery line says "the differences
   persist: a synchronization or mapping defect to investigate", the unit is listed, and the gate panel shows Dubai as "Reconciled; some units held back" with that unit under *Held back*.
   `dbo.CollectionsRcvRun` shows exactly one `Reconciliation` attempt for the run (no retry loop).
4. **Partial refresh failure.** Make the UAT linked server unavailable for one company (or lower `RefreshCommandTimeoutSeconds` to 5 for the test) and press *Run (both companies)*.
   Expected: the affected company shows "could not be refreshed ... the previous snapshot is still in use", its previous snapshot is still served on Receivables (same last-success time,
   same rows), the other company's result is unaffected.
5. **Stale snapshot.** Stop the API for longer than `ReconciliationMaxSnapshotAgeMinutes`, start it with `RefreshOnStartup=false`, press *Dubai only*. Expected: no "minutes old" error; the
   recovery line says the snapshot was too old and was refreshed first, then Passed.
6. **Gate without sign-off.** With `FinancialSourceValidated=false`, Campaigns Manual mode shows every clean customer **Ready** and the status strip says the source is not reconciled
   (SignOffMissing); *Approve & Send* is blocked by that gate alone. Export for Genesys still downloads, labelled "Financial data not verified".
7. **Per-company gate.** Set `FinancialSourceValidated=true` (UAT only). With both companies Passed, Manual mode's summary says the source is reconciled. Make Sharjah's direct read fail
   (wrong `PACTRPT` password for one run) and run *Sharjah only*: the gate shows Sharjah "Not reconciled" and Dubai "Reconciled". A Manual list filtered to Dubai is accepted at approval
   (no upload happens: live dispatch is off); a list containing Sharjah is refused with "not reconciled for Tiger Group Sharjah" and names Dubai as still sendable.
8. **Held unit.** With step 3's persistent difference in place and the sign-off set, refresh the approval data. Expected: that unit is **Needs review** with reason "Snapshot differs from PACT
   after a refresh (investigate)"; every other unit of the company is Ready; `Counts.Ready` excludes only that unit.
9. **Refresh between approval and send.** Approve a Manual list (dispatch stays queued because live dispatch is off; or, with the Genesys sandbox, allow it to execute). Press *Retry* on the
   company on Receivables so a new publish happens before the dispatch executes, and stop the after-refresh check from passing (step 7's failure). Expected: the dispatch ends
   **ReviewRequired** with "not reconciled for ... republished ... has not been checked yet"; nothing was uploaded.
10. **Nothing auto-enabled.** After every step above, `Collections:Campaigns:FinancialSourceValidated` and `AutoSendingEnabled` are exactly what was configured; no run changed them
    (they are configuration only). The six Genesys CSV columns are unchanged (`GenesysContactTemplate`).
