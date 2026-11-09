# Collections campaigns preview and CSV export

> **Current behaviour (rebuilt snapshot):** campaigns read the local receivables snapshot refreshed from PACT every two hours (see [Receivables-Snapshot.md](Receivables-Snapshot.md)). The stage rules and the preview date
> are unchanged; *All months* (no From date) READS the whole history through the end of the current Dubai month, while which units are eligible and their stage amount still come from the configured start date (or the explicit From) - the two scopes are separate. Each row is one apartment of a customer with **Due** (due inside the current Dubai month, any day - including the days still to come), **Overdue** (due before the first day of the current month) and **Total** (= Due + Overdue), all on the REMAINING amount of unpaid or partly paid instalments; instalments due from the next month on are in neither and are never listed, totalled or exported. The month is always the current one: the Month / Year filter and the From / To dates only narrow the results. **Eligibility and reminder timing are not widened by this:** the default To is the end of the month for every stage, but a unit is a candidate, and its stage amount, flags and review reasons are decided from instalments up to today only (`StageRange` and `ScheduledDates` are unchanged); the days of the month still to come feed only the shown Due / Total. A unit whose only unpaid instalments are still to come is therefore not a candidate (as before).
> **Minimum Total (AED)** (default 100, clearable) is applied to the apartment's summed Due + Overdue, strictly greater, before the count, the paging and both exports. Units without a mobile or e-mail are still
> listed (with a reason). Order: newest tower first (tower number descending: TP140 ... TP119), then the unit inside the tower, then company / customer (over the whole result, before paging). Contacts: CRM first, PACT completing only a missing field; when CRM linking is on but CRM cannot be used every unit is `CrmUnavailable` (needs review) and no Genesys file can be exported. Exports (review and Genesys) are refused unless every company in scope has a successful refresh no older than
> `Collections:ReceivablesSnapshot:MaxAgeMinutes` (180). The stage evaluation runs in the application on the payment rows `usp_CollectionsRcv_GetLines` returns (the SQL campaign engine of the previous pipeline was retired).

Collections staff can preview one communication stage per apartment at `/Collections/Campaigns`, reached from
`/Collections/Receivables` through the Campaigns link. The page uses the direct PACT receivables source for companies 4 and 32.

**Filter-first workflow.** The page opens with the filters only: Campaign stage, Project / Tower, Payment status and Period
(Up to today, This month, Last 6 months, Custom range) in the main row; Preview date, From / To date and Minimum Total under the
collapsible *Advanced filters* (open automatically when one holds a non-default value). Period only fills the visible From / To
dates, which remain the single source of the range. The customer search sits above the results but belongs to the same form.
No customer query runs until **Preview Customers** is clicked; changing any filter afterwards clears the customers and ticked
selection and requires a new preview. The financial snapshot status (load problems with Retry, CRM state, reconciliation) is a
separate region that filter changes never clear. **Approve & Send** is disabled unless the preview is current, the financial
source is reconciled and eligible customers are selected (ticked, or *all eligible customers matching the filters*). Nothing is
sent to Genesys until the confirmation step. Script tests: `tools/ui-tests` (`npm install && npm test`).
It does not add CRM or other-company coverage. It creates no campaign, sends no message, schedules no job, creates no ticket,
and opens no legal case. The existing reminder dispatcher and its rule configuration are unchanged.

## Confirmed policy decisions

The user selected the **schedule table** in Collections Communication Script Final, dated 7 October 2026, and **per-unit**
thresholds on 8 October 2026. These choices resolve the table/body date differences for this preview.

| Stage | Schedule in Asia/Dubai | Qualifying remaining principal per unit |
| --- | --- | --- |
| OverdueReminder | Day 1 | Instalments due strictly before the preview date minus one calendar month |
| CurrentMonthReminder | Day 14 | Positive remaining balances with due dates anywhere in the preview month, including upcoming dates |
| FollowUpReminder | Day 28 | Positive remaining balances with due dates anywhere in the preview month |
| LegalNotice | Days 12 and 14 | Previous calendar month's remaining balances, summing to strictly more than AED 1,500 |
| LegalReferral | Day 30, or the last day in February | Remaining balances due strictly more than three calendar months before the preview date, summing to strictly more than AED 20,000 |

Settled rows are removed before evaluating eligibility. Partly paid rows contribute only their reported remaining amount.
Calendar months, rather than fixed 30/90 days, are the implementation convention; referral thresholds apply to the qualifying
aged balance itself, rather than adding newer arrears to an old debt. Fees and penalties are not added. Confirm these amount
conventions against Collections' records during UAT before releasing campaign exports.

A selected past or future preview date changes eligibility against **live** remaining balances; it is not a historical
statement and cannot authorize a Genesys export. Both CurrentMonthReminder and LegalNotice can qualify on day 14; the preview
does not choose a priority or coordinate attempts across stages. Campaign coordination remains part of the Genesys integration.

## Data validation and export modes

Preview requires the existing Collections financial-read grant. Both export modes require the existing reminder-send grant
(CS Supervisor, CS Manager, members holding a configured Collections department role, configured integration accounts,
or the System Administrator central override). Authorization runs before external reads.

* **Review CSV** exports all matching records across pages, with their status/reasons and `Use=InternalReviewOnly`.
  Channel eligibility flags are false. This is a staff review file, not a customer campaign file. Spreadsheet formula
  prefixes are neutralized, including the leading plus sign in phone numbers. Do not import this file into Genesys.
* **Genesys CSV** re-reads PACT and requires every matching record to be Ready, the selected date to be today in Dubai and
  scheduled for the chosen stage, and at least one record. It refuses the whole export if a selected record needs review;
  it does not silently omit unresolved units. `LegalReferral` can never be exported for a customer campaign.
* Exports are bounded by `Collections:Campaigns:MaxExportRows` (default 5,000). Exceeding the limit returns an explicit error,
  with no truncated file. Filter by company or customer to reduce the selection.

`Collections:Enabled=true` and `CollectionsSource:PactReceivables:Enabled=true` expose preview/review using the existing
PACTRPT configuration. Two additional switches default to false:

```json
{
  "Collections": {
    "Campaigns": {
      "FinancialSourceValidated": false,
      "LegalNoticeExportEnabled": false,
      "MaxExportRows": 5000
    }
  }
}
```

`FinancialSourceValidated` must stay false until **remaining amounts and unit allocation** have been reconciled against
actual PACT accounts, including multi-unit customers and partial payments. The original procedures have proven invoice
fan-out and allocation defects, described in `pact-sql/Receivables-Source-Review.md`. Selecting a procedure named V2 does not
establish that reconciliation passed. Ordinary campaign exports become available only after the source is reconciled.
`LegalNoticeExportEnabled` additionally controls release of client-facing legal-notice files.

Even with source validation enabled, rows remain NeedsReview for ambiguous same-date instalments, allocations appearing under
different units of the same customer/date, conflicting unit ids/codes or contact details, invalid/zero unit identity,
positive balances labelled Paid, amounts with more than two decimal places, a non-AED currency, stale/invalid read timestamps,
or no valid phone/email. Same-date rows across units may be legitimate; they are held for review because this source contract
does not expose enough instalment identity to prove that. The page preserves these records instead of deduplicating or guessing.

## API and CSV contract

These are **internal TigerCS API routes**, called server-side by Web as the signed-in user. They are not yet published through
TigerGroupWeb's `/api/genesys` proxy.

```text
GET /api/collections/campaigns/preview?stage=CurrentMonthReminder&businessDate=2026-10-14&companyId=4&page=1&pageSize=25
GET /api/collections/campaigns/export?stage=CurrentMonthReminder&businessDate=2026-10-14&companyId=4&mode=review
```

`businessDate`, `companyId` and `search` are optional. The default date is today in Dubai. Stage names are required and numeric
enum values are rejected. Preview pages accept 1–100 records; company filters accept only 4 or 32. Search is limited to 200
characters. Export modes are `review` and `genesys`; the API returns `{ fileName, csv, rowCount }`. Web serves the CSV as a
UTF-8 attachment with BOM. Financial responses and downloads set `Cache-Control: no-store`.

| CSV columns | Meaning |
| --- | --- |
| RecordId | SHA-256 of company, tenant, unit id, unit code and monthly stage cycle; stable across re-reads in that cycle |
| CustomerKey, CompanyId, TenantId | `ext:Pact:{tenantId}` plus company id; a tenant id alone is not an account identity |
| CustomerName, Phone, Email | Name and validated contacts; Genesys mode preserves E.164 phone strings |
| UnitId, UnitCode, ProjectCode | Source unit/project identifiers; ProjectCode is not necessarily a project name |
| Amount, Currency, DueDate | Qualifying remaining principal, AED, and earliest qualifying due date |
| Stage, CycleKey, ReadAtUtc | Communication stage, monthly cycle, and latest report-read timestamp |
| Status, Reason, Use | Ready/review/preview/internal state, reasons, and intended use of the export |
| VoiceEligible, SmsEligible, EmailEligible | Custom campaign columns to map/filter in Genesys; not claims that Genesys configured contactability |
| TowerNumber, TowerName | **Review export only** (the Genesys file keeps the 22 columns above): the tower number and its real name, the same values the pages show; the name is empty when no source knows one (never invented) |

Numeric amount strings have at least two decimal places with no thousands separators for machine parsing; the display uses
thousands separators. Preview may show source precision for review but cannot export it as Ready. If a unit has several
qualifying due dates, Amount is their sum and DueDate is the earliest; the bot must not describe the sum as one instalment due
on that date. Project names and per-instalment due-date narratives require additional mapping/data before using every template.

For Genesys release, agree the contact-list column mapping, contactListIds, authentication/region, update/upsert semantics,
contactability per channel, handling of several units with the same phone, and priority/attempt limits across overlapping stages.
RecordId is an external stable key, **not** a Genesys-generated contact id and **not** durable delivery deduplication.
Use=InternalReviewOnly and eligibility=false must not be overridden into a live campaign.

## Required non-secret settings (API `appsettings*.json` / environment)

Only names and non-secret values; never commit connection strings or passwords. The local `appsettings.Development.json`
files stay uncommitted.

| Key | Needed value | Purpose |
| --- | --- | --- |
| `Collections:Enabled` | `true` | Otherwise preview returns 503 `CollectionsDisabled` |
| `CollectionsSource:PactReceivables:Enabled` | `true` | Enables the PACT source (committed default is `false`) |
| `CollectionsSource:PactReceivables:StartDate` | default `2026-01-01` | Default `@StartDate`; the Campaigns page overrides it per request with *From date* |
| `CollectionsSource:PactReceivables:CommandTimeoutSeconds` | `120` (1-300) | Per-procedure SQL timeout. API budget = this + 30 s; the Web client timeout (180 s) must stay above it |
| `CollectionsSource:PactReceivables:ProcedureSuffix` | empty, or `V2` once the reviewed procedures are deployed | Procedure selection |
| `CollectionsSource:PactReceivables:MaxSourceRows` | `250000` | Source-row cap |
| `CollectionsSource:PactReceivables:ConnectionStringName` | `PACTRPT` | Name only; the value lives in `ConnectionStrings:PACTRPT` (secret, not documented here) |
| `Collections:Campaigns:FinancialSourceValidated` / `LegalNoticeExportEnabled` / `MaxExportRows` | `false` / `false` / `5000` | See above |
| `ConnectionStrings:TigerCsDatabase` / `PACTRPT` / `CrmDatabase` | committed with an **empty `Password=`** | Supply the password via user-secrets or `ConnectionStrings__PACTRPT` etc.; never in `appsettings*.json` |
| Web `TigerCsApi:BaseUrl` | the API origin, e.g. `https://localhost:7283/` | Web to API calls |

## Floor filter (Campaigns and Receivables)

A searchable multi-select **Floor** sits in *More filters* next to Minimum Total (Campaigns) and next to Minimum Total in the Receivables filter bar. Nothing ticked = **All Floors** = no filtering (every unit, apartment or not).

* **How a floor is identified.** No floor column exists in any data source these pages use: PACT's receivables output, the local snapshot (`CollectionsRcvLine`, `CollectionsRcvAccountUnit`), the review records and the CRM unit-owner feed (CRM's own `tblUnits` floor is not part of `GetUnitOwners`) carry none. The floor is therefore read from the unit number by one rule (`TigerCS.Domain CollectionsUnitFloor`, mirrored in SQL by `dbo.fn_CollectionsUnitFloor`, `database/collections-receivables/V015`): the unit part of `[TP]<tower>-<unit part>` must hold exactly one apartment number of 3 or 4 digits (optionally with one trailing letter; other segments only as a single-letter block/suffix); floor = number DIV 100. `TP140-101` is 1, `TP140-1921` is 19, `TP130-601-A` is 6, `TP136-C-402` is 4, `TP140-001` is the ground floor (0). A code that does not match (parking, retail, `P12`, `12`, two numbers, `*` = cancelled) has **no floor**: it is never offered in the dropdown and never matches a chosen floor; it is never given a guessed one. A tower that numbers its apartments differently (for example floor number in a different position) is therefore listed with no floors, not with wrong ones.
* **The dropdown** lists the floors of the apartment units of the chosen towers (all towers when none): `GET /api/collections/receivables/floors?towerIds=...` reads the distinct unit codes of the local snapshot's unit directory (prefix seek on the `(CompanyId, UnitCode)` index; the published payment lines are the fallback) and classifies them in the application. It reloads whenever the towers change and keeps the floors that still exist ticked. PACT is never contacted and no snapshot refresh is started.
* **Filtering.** Multiple floors are OR-ed; the filter is AND-ed with every other filter (stage, towers, Payment status, dates, Minimum Total, search) and is applied to the finished list of units, so a unit keeps exactly the figures, flags and status it has without the filter; counts, totals, pages and both exports agree. Choosing every listed floor still excludes non-apartment units; "All Floors" (nothing ticked) does not. Auto preview/export: `floors` on `GET /api/collections/campaigns/preview|export` (repeat the parameter); Manual: `Floors` on the review filter (`floors` on `GET /api/collections/review/records`, `Floors` in the `ReviewFilter` JSON of the selection / export bodies); Receivables: `floors` on `GET /api/collections/receivables/instalments` (SQL: optional `@Floors` of `usp_CollectionsRcv_GetUnits`). All are optional: a request without them is exactly what it was.
* **Sending.** The floors are part of the filter that Manual/Auto approvals re-resolve and that the dispatch stores (`CollectionsDispatch.FilterJson`; older stored filters without `Floors` read as All Floors), so the customers previewed are the customers approved and uploaded. The scheduled Auto dispatcher (`AutoDispatchService`) has no user filter of its own and is unchanged; nothing was enabled in any scheduler or background job.
* **Database.** No table or column is added. `V015__fn_CollectionsUnitFloor.sql` (a function) and the re-run of `R004__read_procedures.sql` (optional `@Floors` parameter) are needed ONLY for the Floor filter on the Receivables page; Campaigns (Auto and Manual) work without them. Deploy V015, then R004, before the application version that sends `@Floors`.

## Due-date range (From date / To date)

The page and API accept `dateFrom` and `dateTo` (instalment due dates, inclusive); both are editable and a blank field uses
the default. **From** defaults to the configured receivables start date (`CollectionsSource:PactReceivables:StartDate`, `2026-01-01`), not to 1 January of the preview year, so December arrears stay reachable in January; a preview whose window ends before that date is rejected rather than widened. **To** defaults to the **end of the preview date's month**
for the whole-month stages (Current month, Follow-up) and to the **preview date** for Overdue, Legal notice and Legal
referral. The dates are sent to the PACT procedures as `@StartDate` / `@EndDate` and the company filter selects the
procedure(s) that run, so rows outside the window are never read. The **preview date stays separate**: it alone drives the
schedule and stage eligibility; the defaults do not change any eligibility rule.

### Stage-semantics conflict (flagged, eligibility NOT changed)

* **Overdue reminder** is *scheduled* on day 1; the confirmed rule qualifies instalments due **before the preview date minus one
  calendar month**. It is not "day-1 overdue", and it is not "everything overdue since January". The label now says so and the
  response carries `RangeNotes` stating the cut-off and that instalments before *From date* are excluded.
* **Current month / Follow-up** qualify the whole preview month **including upcoming dates**, so their default *To* is the
  month end. If a user edits *To* to an earlier date, the list is narrower than the confirmed policy and the page shows a
  note.
* **Legal notice / referral** are likewise limited by the window (previous month; older than three months).

## Enabling EmailNotifications and CrmDocuments

Both are `false` in the committed `appsettings.json` (enabling email without a password fails startup validation by design).
Enable per environment with environment variables or user-secrets, never in a committed file. API host, with `__` as the
section separator:

| Setting | Value |
| --- | --- |
| `EmailNotifications__Enabled` | `true` |
| `EmailNotifications__Provider` | `Smtp` (committed default; `Recording` only in Development/Testing) |
| `EmailNotifications__SmtpHost` / `SmtpPort` / `EnableSsl` | committed defaults `smtp.office365.com` / `587` / `true` |
| `EmailNotifications__Username` / `FromEmail` | the sending mailbox (non-secret; committed values may be kept) |
| `EmailNotifications__Password` | **secret, required when enabled**: user-secrets or environment only |
| `CrmDocuments__Enabled` | `true`. Requires working email (the document copy is sent through `EmailNotifications`) and the CRM connection (`ConnectionStrings__CrmDatabase`, secret) |

Local development: `dotnet user-secrets set "EmailNotifications:Password" "<value>"` in `src/TigerCS.Api`. Enabling the flag alone does not make
document copies work with `Crm:Provider=Http`: the CRM document gateway is not implemented yet and requests answer 503
`DOCUMENT_SOURCE_UNAVAILABLE` (see `docs/Genesys/Document-Copy-API.md`). With
`CrmDocuments:Enabled=false` the document-copy API answers 503 `DOCUMENT_COPY_DISABLED`; see `docs/Genesys/Document-Copy-API.md`.

## Why the preview used to hang (root cause)

> **Status: UNVERIFIED against real PACT.** The cause below is inferred from the code, the logs supplied and the existing
> procedure review. Nobody has measured `p4AccountReceivables` for this call or reproduced the cancellation against real
> PACT. Confirm with the new log lines (per-procedure elapsed ms, budget/caller cancellation) before treating it as proven.


* `CollectionsCampaignAppService.PreviewAsync` always read **both** company procedures (`p4`, `p32`) and only then applied the
  `companyId` filter in memory. Company 32 finishing in ~874 ms (3,750 rows) says nothing about company 4: its procedure is
  documented at about 58 s even with a narrower filter (`pact-sql/Receivables-Source-Review.md`) and the log showed no
  completion line for it. The request therefore stayed pending until p4 finished or the 150 s API budget / 180 s Web timeout
  or the user's refresh aborted it. The `TaskCanceledException` in `ClientDisconnectMiddleware` is the aborted request
  (`RequestAborted` cancelled), a symptom, not the cause.
* Fix: company and due-date window are pushed into the source (a company-32 request no longer runs or waits for p4);
  timeouts, caller cancellation and source failures are logged with stage, company scope, window, elapsed ms and failure kind
  (no row content, no credentials). All-company requests still wait for the slower procedure; fixing that needs the reviewed
  V2 procedures or a validated snapshot cache, and has not been done.
* UI: the previous error is hidden when a new request starts, and a 170 s watchdog stops a navigation that never answers,
  re-enables the button and shows a message.

## Next integration stage

The proposed integration will synchronize records to Genesys, suppress/update them after payment and return channel results.
The CSV read verifies payment state at export time only. It cannot cancel a queued contact after a later payment; revalidation
before contact and suppression in Genesys must be implemented before automated live campaigns. A CSV download is not recorded
as a sent reminder, and downloading it twice does not prevent two manual imports.

Response tickets must be linked to the customer/unit/campaign and routed to Collections only on actual customer response,
using the existing ingestion/outcome workflow where appropriate. The new preview/export does not create them.

## UAT checks

1. Open Campaigns as a Collections sender and verify preview plus review export; a financial-read-only agent can preview but
   cannot export. Anonymous and Reporting User calls must fail before PACT is read.
2. Verify two different units of one customer and the same tenant id in companies 4 and 32 stay separate.
3. Reconcile a partial payment, a settled unit, old arrears and upcoming month instalments against PACT. Confirm exact
   one-/three-month age boundaries and exact AED 1,500/20,000 balances do not meet strict thresholds.
4. Verify ambiguous allocations, unit 0 and stale reads are visible for review and cannot enter a campaign file.
5. Download a multi-page review, pay/remove an account before a second export, and confirm the fresh file excludes it.
6. Confirm a February referral uses the last day, follow-up stays on day 28, and LegalReferral remains internal.
7. Only after source reconciliation, test Genesys export on an actual scheduled Dubai date; unresolved rows, different dates,
   source failures and over-limit selections must fail explicitly rather than produce a partial campaign.
