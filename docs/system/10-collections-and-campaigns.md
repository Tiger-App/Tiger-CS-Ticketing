# 10 - Collections and campaigns

Status: code audit of the tree at `31878f4` (Collections code unchanged since `a1cba71`). The code is the source of truth. Existing `docs/Collections/*` may be stale where they disagree. Systems we could not reach (PACT, EDSM, PACTRPT stored procedures, CRM, Genesys, TigerGroupWeb) are marked **UNVERIFIED EXTERNAL**. Labels: **Verified (code)**, **Ambiguous**, **Draft/Proposed**, **Unverified external**.

## 1. What exists, in one table

| Capability | State | Gate in committed config |
|---|---|---|
| Due & Overdue receivables list (PACT companies 4 and 32, one row per apartment) | Implemented, read-only | `Collections:Enabled=true`, `CollectionsSource:PactReceivables:Enabled=true` (both **on** in `appsettings.json`; connection-string passwords are blank, so it only works when a password is injected) |
| Campaign preview (5 stages) and CSV export (review / genesys modes) | Implemented, read-only | on, but Genesys-mode export is blocked until `Collections:Campaigns:FinancialSourceValidated=true` (default false) |
| EDSM payment summary / payment transactions (Payment tab and Genesys) | Implemented | `CollectionsSource:EdsmProvider="Pact"` in config; needs `PactApi:ApiKey` (not committed) |
| Next payment | Implemented, **off** | `CollectionsSource:NextPayment:Enabled=false`; also needs a per-company attestation |
| Due-installments (EDSM, company-wide) | Implemented, **off** | `CollectionsSource:DueInstallmentsEnabled=false` |
| Outstanding balance, instalment schedule, posted payment history (`ICollectionsFinancialSource`) | Port + rules implemented; **no real adapter** | `CollectionsSource:Provider="Unavailable"` -> every call 503 `FinanceUnavailable`; `Fixture` only in Development/Testing (startup refuses elsewhere) |
| Reminder candidates, queue, dispatch, outcomes, history | Implemented on top of that port; effectively unusable until a real financial source exists | `Collections:Channels:*Enabled=false`, `SchedulerOwner="None"`, `BusinessRulesConfirmed=false`, `BackgroundJobs:Enabled=false` |
| Genesys outbound campaign **sync** (pushing contact lists into Genesys) | **Not implemented.** The only hand-off is a manually downloaded CSV | n/a |
| SMS | No provider exists; fails closed at dispatch | `SmsEnabled=false` |
| Arabic reminders | Accepted by the API (`language: ar`) but the email provider refuses `ar` permanently (no approved template) | n/a |

## 2. Pages, routes and access

| Surface | Route | Access (code) |
|---|---|---|
| Web nav "Collections" | `/Collections/Receivables` (`_Nav.cshtml:14`) | **Shown to every signed-in user**; `AuthorizeFolder("/")` only requires login (`TigerCS.Web/Program.cs:15`). The API decides. A user without a grant sees "Your account does not have permission to view Collections financial data" (`Receivables.cshtml:126-129`). |
| Web campaigns page | `/Collections/Campaigns` | same |
| API receivables | `GET /api/collections/receivables/customers` | `AuthenticatedStaff` + `CanReadFinancials` |
| API campaigns | `GET /api/collections/campaigns/preview|export` | preview: `CanReadFinancials`; export (both modes): `CanSendReminders` |
| API reminders | `reminders/candidates`, `POST reminders`: `CanSendReminders`; `POST reminders/{id}/outcomes`: `CanReportOutcomes` (integration accounts only) | |

Role resolution (`CollectionsCore.cs:65-90`, defaults in `CollectionsOptions.cs:73-87`):

| Grant | Who (default) |
|---|---|
| `CanSendReminders` | any account whose id is in `Collections:Authorization:IntegrationEmployeeIds` (ships **empty**); OR holds Department Employee / Department Head **and** belongs to the active department with code `COL`; OR holds CS Supervisor / CS Manager; OR System Administrator |
| `CanReadFinancials` | `CanSendReminders`, or CS Agent, CS Supervisor, CS Manager, General Manager, Chairman/CEO; or System Administrator |
| `CanReportOutcomes` | integration accounts, or System Administrator (central override) |
| Not allowed | Reporting User; Department Employee/Head outside `COL` |

The `COL` department comes from workflow reference data (`WorkflowReferenceData.CollectionsCode`); matched by code, not name. Role lists are configuration (`FinancialReadRoles`, `ReminderSendRoles`, `CollectionsDepartmentRoles`).

## 3. Receivables list (PACT Due & Overdue)

### 3.1 Source (Verified code, procedure behaviour Unverified external)
* `PactSqlReceivablesSource` runs the stored procedures `dbo.p4AccountReceivables{Suffix}` and `dbo.p32AccountReceivables{Suffix}` on the database named by connection string `PACTRPT` (`ProcedureName`, `PactSqlReceivablesSource.cs:295-301`). Suffix is `PactReceivables:ProcedureSuffix` (default empty = deployed originals; `V2` = drafts in `docs/Collections/pact-sql`, "not deployed").
* Parameters: `@StartDate` (default `PactReceivablesOptions.StartDate` = **2026-01-01**), `@EndDate` = last representable `datetime` of the through-date, `@MinAmount = 0` (`:242-245`). Both companies run in parallel; any failure fails the whole read, no partial list (`:196-218`).
* Required columns: `CompanyID, TenantID, FullName, Mobile, Email, UnitID, UnitCode, VoucherNumber, ChequeNumber, DueDate, Amount, Status` (`ProjectCode` optional). A null `DueDate` or `Amount` throws and fails the whole read (`:256`).
* Limits: `CommandTimeoutSeconds=120`, request budget = timeout + 30 s, `MaxSourceRows=250000` (exceed -> error, no partial result).
* Optional legacy exclusions (`ApplyLegacyExclusions=false` by default): CRM `tblPayment`/`tblLead` down-payment keys, special-case unit codes (50 listed in config, but unused while exclusions are off), TP101-TP104 units, and projects TP121/122/123 after cut-off dates (`ApplyExclusions`, `:351-363`). `SpecialCasesConfigured=true` is set in config without the flag being needed.

### 3.2 Filters and selection (`PactReceivableCustomersAppService.ListAsync`)
1. Authorize first, then check `Collections:Enabled` and `PactReceivables:Enabled` (else 503 `CollectionsDisabled`).
2. Validate: `companyId` null/4/32, `status` all/due/overdue, `page>=1`, `pageSize 1-100`, `search<=200`, `year` and `month` together.
3. Reporting month defaults to the current Dubai month; the read goes through the month's last day (`:34-46`).
4. Row filter: **`Amount > 0` and `DueDate <= period end`** (`PactReceivableCustomersAppService.cs:67`). Zero and negative amounts (credits/overpayments) are silently dropped. Rows due after the month end ("Outstanding" in EDSM terms) are never listed.
5. Rows must have company 4/32 and a non-blank TenantID, else the whole call is 503 (`:68-70`).
6. Grouping: one customer row per **company + tenant + unit id + unit code** (an apartment), `:376`.
7. Search: name, tenant id, email, mobile, unit code; digit-only terms (>=3 digits, no letters) match phone digits.
8. Order: has-overdue first, earliest due date, name, company, tenant, unit.

### 3.3 Meaning of the fields (what the code does vs what is assumed)
| Term | Meaning in code | Status |
|---|---|---|
| `dueDate` (instalment) | `DueDate` column of the PACT procedure result, converted to a date. | Column mapping **Verified (code)**; business meaning (instalment due vs cheque value date) **Ambiguous / Unverified external**. `docs/Collections/pact-sql/Receivables-Source-Review.md` says it filters the final `b.DueDate` of the pay-term row. |
| `remainingAmount` (`Amount`) | Value of the `Amount` column. Review doc reads the SQL as `Amount = NewFutureAmount`, i.e. the remaining part of that instalment after FIFO allocation of tag-level payments. | Code mapping verified; allocation correctness **Unverified external**. Review doc lists confirmed defects in the originals: instalment rows repeated accounts x invoices times, same-date double counting, float arithmetic (D1-D8). |
| Original amount / scheduled amount | **Not returned.** The list has no original amount. (`FinancialInstalment.ScheduledAmount` exists only in the unused financial-source port.) | Gap |
| `receivablesType` | `OverDue` if due date < first day of reporting month, else `Due` (month incl. later days) (`PactReceivableCustomersAppService.cs:135`). Computed in TigerCS. | Verified (code) |
| `dueTiming` | `Overdue` (< today), `DueToday`, `Upcoming` - against the Dubai business date. | Verified (code) |
| `paymentStatus` | `Paid` only if source status text is `Paid` **and** remaining <= 0; source `Installment` (and blank) -> `Unknown`; any other value maps only through `SourceStatusMap` (config, ships empty) else `Unknown`. **"Unpaid" vs "PartiallyPaid" is therefore never derived** and, with an empty map, never appears. | Verified (code). The review doc says the procedures emit only `Paid` / `Installment`; **Unverified external**. |
| `dueAmount` / `overdueAmount` / `totalAmount` | Sum of remaining amounts in each bucket, **null (`NeedsReview`) when two instalments share a due date inside the bucket** (cannot tell duplicates from legitimate instalments) (`PactReceivableCustomersAppService.cs:144-150`). Display shows "Review needed" (`Receivables.cshtml.cs:56`). | Verified (code) |
| "Unpaid" | Not a field. Rows exist only while remaining > 0, i.e. there is no list of paid items. | |
| Null credits | PACT list: a null `Amount` aborts the read (503). EDSM owned companies: "instalments with no credit recorded at all are excluded by EDSM" (field definitions, `CollectionsPaymentSummaryAppService.cs:616-617`) and the semantic doc marks completeness **UNVERIFIED** (an untouched future instalment may not appear). Financial-source port: `AppliedCreditAmount = null` means unknown and makes `AmountDueNow` null, never zero (`AccountBalanceCalculator.cs:93-95`); negative credit = `InvalidSourceData`. | Verified (code) / Unverified external |

### 3.4 Outstanding balances, payment history, instalments
* **PACT/EDSM path (implemented, UI Payment tab and Genesys):** `GET .../customers/by-key/{ext:Pact:tenant}/payment-summary` resolves the customer's (companyId, tenantId) pairs through PACT `v1/contracts/{mobile}` (this call **writes inside EDSM** - accepted side effect), then calls EDSM `v1/reports/payment-summary`, `v1/reports/payment-transactions` (types Paid/Due/Outstanding; type All refused because it writes for rented companies) and, if enabled, `v1/due-installments`. EDSM host/API key = `PactApi:BaseUrl` / `PactApi:ApiKey` header `X-API-KEY`. TigerCS shows EDSM's figures and never recomputes. Amounts are strings formatted `#,##0.00`, read only if `EdsmNumberCulture` is set (committed `en-US`, **Unverified** against the IIS host). Currency is assumed AED (EDSM returns none).
* **Owned (4, 32) field meanings, from EDSM code via comments:** Paid = sum of credits; Due = unpaid remainder (debit - credit) of instalments with cheque due date <= EDSM server date; Not yet due = remainder of later ones; late fines = rows whose description contains "fine" (> 0); total = paid + due + not yet due. Rented companies (25, 7, 20) have different, cheque-based definitions.
* **Freshness:** the response carries `retrievedAt`, `sourceCacheMinutes=10`, `maxSourceDelayMinutes=20` ("a posted payment can take up to ~20 min to appear", deployed values **Unverified**). The receivables list carries `readAtUtc` only.
* **Instalment schedule / posted payment history / per-account outstanding** (`customers/{crmCustomerId}/outstanding|payments`): fully implemented rules (remaining principal, penalties/fees, applied credit, reconciliation `Mismatch`), but the only registered sources are `Unavailable` (503) and `Fixture`. **No real adapter exists.** Do not describe these as live.
* **Unavailable data is explicit**: a missing PACT mapping -> `mappingStatus: NotMapped` (transactions route 422 `CustomerNotMapped`); a company EDSM cannot answer -> that company's `status` carries the outcome and `fields` is empty; deadline exceeded -> `DeadlineExceeded`; the summary carries `completeness` (Complete/Partial/NoFigures) and `incompleteReasons`. All-zero EDSM answers get a note because EDSM returns zeros for unknown tenants.

## 4. Campaigns (preview / export)

### 4.1 Stages (`CollectionsCampaignPolicy`, thresholds hard-coded)
| Stage | Scheduled day (Dubai) | Qualifying remaining amount per unit |
|---|---|---|
| OverdueReminder | 1st | instalments due before (date - 1 month); amount > 0 |
| CurrentMonthReminder | 14th | due in the preview month (incl. future days); > 0 |
| FollowUpReminder | 28th | same as current month; > 0 |
| LegalNotice | 12th and 14th | due in the previous calendar month; sum **> AED 1,500** |
| LegalReferral | 30th (min with month length, e.g. 28/29 Feb) | due more than 3 months before; sum **> AED 20,000**; internal review only, never exported to Genesys |

Day-14 CurrentMonth and LegalNotice both apply to the same unit; campaign types are independent, so the unit is eligible in each (no cross-stage priority, collision hold or de-duplication) (`docs/Collections/Collections-Campaigns.md` says so too).

### 4.2 Prepare
`PreviewAsync` reads PACT with a company- and window-scoped request (`from` defaults to the configured **`PactReceivablesOptions.StartDate`** (2026-01-01) and a window ending before it is rejected; `to` defaults to month end for CurrentMonth/FollowUp, else the preview date) (`CollectionsCampaignAppService.cs:42-57`), keeps `Amount > 0` rows in the window, groups by company+tenant+unit, evaluates the stage, then assigns a status per unit:
`Ready` | `NeedsReview` | `PreviewOnly` (`OutsideSchedule`: date not today or not a scheduled day) | `InternalReview` (LegalReferral). Reasons: `AmbiguousInstalments`, `AmountPrecisionNeedsReview`, `MissingUnitIdentity`, `ConflictingContactDetails`, `UnitAllocationNeedsReview` (instalment duplicated under several units of a tenant), `ContradictoryPaymentStatus` (source says Paid yet amount > 0), `CurrencyNeedsReview`, `StaleSource` (read older than `StaleAfterMinutes=60`), `SourceReconciliationRequired` (**always**, until `FinancialSourceValidated=true`), `NoValidContact`, `OutsideSchedule`, `LegalNoticeReleaseRequired` (until `LegalNoticeExportEnabled=true`), `InternalLegalReferralOnly`. Phones are normalised to E.164 (UAE `05xxxxxxxx`/`971...`/`00...`); emails must parse exactly.
Record id = SHA-256 of `company:tenant:unitId:unitCode:yyyy-MM:stage` (stable per cycle).

### 4.3 Export (manual CSV, no sync)
* `mode=review`: all matching rows with status/reason, `Use=InternalReviewOnly`, eligibility flags false, formula-injection neutralised.
* `mode=genesys`: refused unless the date is today (Dubai) **and** a scheduled day **and** every row is `Ready` and the stage is not LegalReferral and there is at least one row (`:187-191`). Because `SourceReconciliationRequired` is added while `FinancialSourceValidated=false`, **Genesys export cannot succeed by default**.
* Row cap `Collections:Campaigns:MaxExportRows=5000` (clamped to 50000) -> 400, no truncated file.
* Output columns: `RecordId, CustomerKey (ext:Pact:{tenant}), CompanyId, TenantId, CustomerName, Phone, Email, UnitId, UnitCode, ProjectCode, Amount, Currency, DueDate, Stage, CycleKey, ReadAtUtc, Status, Reason, Use, VoiceEligible, SmsEligible, EmailEligible`.
* The Web page returns a UTF-8-BOM CSV (`Campaigns.cshtml.cs:107`).

### 4.4 Genesys outbound, duplicate prevention, payment suppression, attempts
| Topic | What the code does |
|---|---|
| Genesys outbound sync | None. No contact-list API client, no scheduler, no write-back. Staff download the CSV and a person imports it (process outside the repo, **Unverified**). |
| Duplicate prevention in campaigns | **None.** The campaign service has no repository dependency; it records nothing, so the same stage can be exported repeatedly and overlapping stages (day 14) can both include a unit. `cycleKey`/`recordId` are stable ids that a downstream system *could* dedupe on. |
| Payment-based suppression in campaigns | Implicit only: each preview/export re-reads live PACT remaining amounts, so a unit paid before the read drops out. Nothing is suppressed *after* export (no dispatch revalidation, because TigerCS does not dispatch campaigns). Paid-in-transit money not yet in PACT is not seen. |
| Attempts / outcomes / follow-up for campaigns | Not tracked. |
| Duplicate prevention in the reminder pipeline (separate feature) | Unique `account \| type \| cycle \| channel` key in the database; `Idempotency-Key` replay returns the original job; same key + different body = 409; retries only for failed channels up to `MaxDeliveryAttempts=3` (`CollectionsReminderAppService.cs:340-374`). Cycle = month (`OncePerWindow`) or day (`Daily`). |
| Payment-based suppression in the reminder pipeline | Candidates are re-evaluated before queueing and **again immediately before each SMS/email send** from a fresh, non-stale source read; settled / ineligible / currency-changed accounts are marked `Suppressed` (`CollectionsOutboxHandlers.cs:65-90`). AlreadyPaid responses open a verification follow-up and **post nothing**. |
| Attempts / outcomes / follow-up | Per-channel status Queued/Sent/Delivered/Answered/NoAnswer/Failed/Suppressed with attempts; outcomes reported by integration account: delivery status and/or customer response (PromiseToPay, AlreadyPaid, RequestedHuman, AiDisconnected, Disputed, Other). A response in a conversation creates/reuses a ticket through Genesys ingestion (routed by `ResponseTickets.QueueId`/`DepartmentCode="COL"`, left Unclassified, never resolved); if the ticket cannot be created the answer is 202 `ticketResult: Pending` and an outbox retry follows. |
| Reminder windows (Dubai) | OverdueMonthly days 1-4 (instalment due before date - 1 month, or fixed days), CurrentMonth day 15, MonthEndFollowUp 3 days before month end (`ReminderRules.cs`). **Drafts**, not confirmed rules: the schedule table of the campaigns feature (1/14/28) disagrees with the reminder feature (1-4/15/month-end-3). |
| Channels / language | VoiceBot (Genesys dials; only the integration account may queue it), SMS (no provider), Email (via `EmailNotifications`, English only). Language values `en`, `ar`. |

## 5. Configuration keys

| Key | Default in code | Committed value | Meaning |
|---|---|---|---|
| `Collections:Enabled` | false | **true** | master gate for every Collections route |
| `Collections:CollectionsDepartmentCode` | COL | COL | department whose Dept roles get grants |
| `Collections:TimeZoneId` | Asia/Dubai | same | business calendar |
| `Collections:StaleAfterMinutes` | 60 | 60 | older figures are stale; stale never authorises a send |
| `Collections:CandidateValidityMinutes` / `MaxDeliveryAttempts` / `MaxAccountsPerScan` | 15 / 3 / 5000 | not set | |
| `Collections:Authorization:FinancialReadRoles|ReminderSendRoles|CollectionsDepartmentRoles|IntegrationEmployeeIds` | see 2 | `IntegrationEmployeeIds: []` | |
| `Collections:ResponseTickets:DepartmentCode|QueueId` | COL / null | COL / null | where reminder-response tickets land |
| `Collections:Channels:VoiceBotEnabled|SmsEnabled|EmailEnabled|ScheduledChannels` | false x3 | false x3 | sending gates |
| `Collections:Rules:*` | CalendarMonth, 30, 1-4, 15, 3, IncludePenaltiesAndFees=false, OncePerWindow | not set | proposed drafts |
| `Collections:SchedulerOwner` / `BusinessRulesConfirmed` / `ScheduleCron` | None / false / `0 9 * * *` | None / false | TigerCS scheduler runs only with Enabled + Confirmed + owner `TigerCS` + `BackgroundJobs:Enabled` |
| `Collections:Campaigns:FinancialSourceValidated|LegalNoticeExportEnabled|MaxExportRows` | false / false / 5000 | **absent** (defaults apply) | release switches |
| `CollectionsSource:PactReceivables:*` | Enabled=false, StartDate 2026-01-01, CommandTimeoutSeconds 120, MaxSourceRows 250000, ApplyLegacyExclusions false, ProcedureSuffix "" | Enabled **true** | |
| `ConnectionStrings:PACTRPT`, `CrmDatabase` | - | server/user committed, `Password=` empty | secrets must be injected |
| `CollectionsSource:Provider` | Unavailable | Unavailable | financial source (Fixture only Dev/Test) |
| `CollectionsSource:EdsmProvider` | Unavailable | **Pact** | EDSM via PactApi |
| `CollectionsSource:EdsmNumberCulture`, `Currency`, `TransactionsEnabled`, `DueInstallmentsEnabled`(false), `DueInstallmentsLookback/LookaheadDays`(31), `NextPayment:*`(off), `SourceCacheMinutes`(10), `MaxSourceDelayMinutes`(20), `PactMapping(Negative)TtlMinutes`(30/5), `GenesysReadDeadlineSeconds`(22), `WebReadDeadlineSeconds`(60) | | | |
| `PactApi:BaseUrl`, `PactApi:ApiKey` | | `http://10.30.10.117:6020/` (plain HTTP, internal), key **not committed** | |
| `BackgroundJobs:Enabled` | | false | no Hangfire, no outbox dispatch, no scheduled reminders |

## 6. What is off by default (summary)
Sending of any kind (all channels false, scheduler None, business rules unconfirmed, background jobs off); Genesys campaign export (`FinancialSourceValidated=false`); legal notice export; next payment; due-installments; document/OTP flow; email notifications. What is **on** by default and reaches production data when credentials are supplied: the receivables list, campaign preview/review export, EDSM payment summary reads.

## 7. Unverified external assumptions to confirm
PACT procedure semantics (Status vocabulary, `Amount` meaning, same-date duplicates); whether `V2` procedures are deployed; EDSM number culture; EDSM cache timings; that UAE business wants the 1/14/28 schedule versus 1-4/15/month-end-3; how CSVs are imported into Genesys and who prevents repeat contact; SMS provider; Arabic templates.

## 8. Verification of campaign behaviour

Scope: the approved policy - schedule table of 7 Oct 2026, per-unit thresholds of 8 Oct 2026 - against `CollectionsCampaignAppService`, `CollectionsCampaignPolicy` and `CollectionsCampaignCsv`. Everything below was checked with the in-memory source double (`CollectionsCampaignAppServiceTests`, `CollectionsCampaignVerificationTests`, `CollectionsCampaignPolicyTests`); **no row was read from real PACT.**

### 8.1 Verified by test

| Item | Result | Test |
|---|---|---|
| `PactReceivablesOptions.StartDate` is the only lower bound | Campaign preview, review CSV and Genesys CSV all call the source with `FromDate = StartDate` (a non-default value is honoured, so the option itself is what is read); the receivables list passes `FromDate = null`, which `PactSqlReceivablesSource` resolves as `request.FromDate ?? options.StartDate`. A source scan fails the build if a literal `2026-01-01` lower bound reappears outside the option class | `TheConfiguredStartDate_IsTheLowerBound_...`, `TheReceivablesList_AsksTheSourceForItsConfiguredStart_...`, `NoCollectionsCodeHardCodesTheStartDate_...` |
| **Defect fixed:** calendar-year fallback | When `StartDate` was after the window end, the lower bound silently became 1 Jan of the preview year (pre-`StartDate` rows would be read). It is now `400 InvalidRequest` (nothing is read). The Campaigns page text and two documents still said "1 Jan of the preview year"; corrected | `AWindowEndingBeforeTheStartDate_IsRefused_...` |
| An explicit *From date* overrides `StartDate` | Documented behaviour, pinned | `AnExplicitFromDate_StillOverridesTheStartDate_...` |
| Preview year 2026 (8 Oct 2026) | Feb 2026 arrears, a 21,000 account (overdue + referral), a September balance > 1,500 (legal notice) are retained; a Dec 2025 row (before `StartDate`) is excluded from every stage | `Preview_8Oct2026_...` |
| Preview year 2027 (1 Feb 2027) | Dec 2026 and Oct 2026 arrears stay in OverdueReminder, the Oct 2026 account stays in LegalReferral, the Jan 2027 balance is the LegalNotice; a Dec 2025 row is excluded | `Preview_1Feb2027_...` |
| Preview 1 Jan 2027 | Dec 2026 is the LegalNotice month (not yet overdue-by-a-month), Oct 2026 qualifies for OverdueReminder, Aug 2026 > 20,000 for LegalReferral | `Preview_1Jan2027_...` |
| Stage rules and boundaries | Overdue = due strictly before date - 1 month; current/follow-up = whole preview month incl. upcoming; legal notice = previous month, sum **> 1,500**; referral = due strictly before date - 3 months, sum **> 20,000**; schedule days 1 / 14 / 28 / 12+14 / 30 (last day in February) | `CollectionsCampaignPolicyTests` (existing) |
| One contact per unit per stage | Several instalments of one unit = one contact, amount = sum, `DueDate` = earliest | `OneUnitIsOneContactPerStage_...` |
| Per-unit thresholds | Two units of one customer at 1,000 each (2,000 in total) produce no legal notice; two units each above 1,500 are two contacts with different `RecordId`, same `CustomerKey` | `ThresholdsArePerUnit_...` |
| Paid / settled / credits | Amount 0 and status `Paid`: removed. Part-paid: only the remaining amount. A negative (credit) row is ignored and is **not netted** against the customer's other arrears. Positive amount labelled `Paid`: `NeedsReview` (`ContradictoryPaymentStatus`) | `SettledRows_CreditsAndPartPayments_...` |
| Missing data | `UnitId` null/0 -> `MissingUnitIdentity`; no valid phone and email -> `NoValidContact`; neither can enter a Genesys file. A null amount or due date from the SQL source throws `PactReceivablesSourceException` (fail closed, code reading only - not testable without SQL Server) | `MissingIdentityAndContacts_...`, existing `InvalidUnitCannotEnterAGenesysCampaign` |
| Day 14 overlap | A unit qualifying for both CurrentMonthReminder and LegalNotice appears in **both** lists; each campaign is evaluated independently: no priority, no collision hold, no cross-stage de-duplication; Already Sent is scoped to the same campaign type | `OnDay14_BothCurrentMonthAndLegalNoticeQualify_...` |
| Allocation ambiguity scope | `UnitAllocationNeedsReview` is judged over the **whole read window** (`StartDate`..To), not only the stage's qualifying rows: a same-date pair under two units in March also flags the tenant's October contact. Stricter than necessary; pinned, not changed | `AllocationAmbiguity_IsJudgedOverTheWholeReadWindow_...` |
| CSV columns | The 22 header columns equal the documented table in `docs/Collections/Collections-Campaigns.md`, in order (doc/code drift fails the build) | `TheCsvColumns_AreExactlyTheDocumentedOnes_...` |
| Formula injection | A text cell starting with `=`, `+`, `-`, `@`, tab (or leading spaces then one of them) gets a leading `'` in both modes; a Genesys file keeps the validated `+971...` phone, a review file neutralises it | `EveryCsvTextCell_...`, `ArabicNames_AreKeptExactly_...` |
| Arabic and quoted names | Arabic text, quotes and commas survive both modes unchanged (CSV-quoted); the Web adds the UTF-8 BOM | `ArabicNames_AreKeptExactly_...` |
| Records nothing | The campaign service has no repository, unit-of-work, outbox or audit dependency (constructor check), so preview and both exports create no record, ticket or suppression | `ThePreviewAndExport_HaveNoRepositoryOutboxOrAuditDependency_...` |
| Export limits, agents, stale source | Existing coverage: row cap rejects instead of truncating; agents preview but cannot export; stale/failed reads are explicit | `CollectionsCampaignAppServiceTests` (existing) |

### 8.2 "Outcomes" that exist today

Only the **reminder pipeline** has outcomes: `POST /api/genesys/collections/reminders/{reminderId}/outcomes` (integration account, `Idempotency-Key`) records per-channel delivery status (Queued/Sent/Delivered/Answered/NoAnswer/Failed/Suppressed) and the customer response (PromiseToPay, AlreadyPaid, RequestedHuman, AiDisconnected, Disputed, Other), and creates or reuses a ticket through Genesys ingestion. It is keyed by a `reminderId` created by `POST .../reminders` for the reminder types OverdueMonthly / CurrentMonth / MonthEndFollowUp. **A campaign CSV record (`RecordId`) is not a reminder**: nothing links a CSV row to an outcome, a download is not recorded as a send, LegalNotice and LegalReferral have no outcome type at all, and no attempt or payment-promise is stored for campaign lists.

### 8.3 Must still be confirmed by Collections (not decidable from the code)

* **Amount conventions:** remaining principal only (no fees or penalties), calendar months for the 1- and 3-month ages, referral and notice thresholds applied to the qualifying aged balance per unit, strict `>`, and a credit row **not** reducing other arrears. Policy text itself says to confirm these against Collections' records during UAT.
* **The default lookback change:** the default *From date* is the configured `StartDate` (2026-01-01) instead of 1 January of the preview year. It makes 2027 previews see 2026 arrears (verified above), but also widens what the allocation-ambiguity check inspects (8.1) and means the receivables list and campaigns show nothing due before `StartDate` by design. Collections must confirm that no pre-2026 arrears are expected in campaigns, and who moves `StartDate` and when.
* **Priority on day 14:** whether a unit that is in **both** CurrentMonthReminder and LegalNotice on day 14 gets one contact (which one) or both. The code applies no priority; coordination is a Genesys-side decision not yet made.
* **Unfixable / out of scope here:** real-PACT reconciliation (`FinancialSourceValidated` stays false), the SQL procedures' allocation and fan-out defects, whether credits belong in the Collections amount, the legacy-exclusion constants in `PactSqlReceivablesSource.ApplyExclusions` (`TP121/TP122/TP123` cut-off dates, off by default with `ApplyLegacyExclusions=false`; they reproduce an EDSM function and are not a lower bound), payment suppression after export, and anything on the Genesys side (contact-list mapping, upsert semantics, attempt limits).
