# Collections review and Genesys dispatch: UAT readiness

Builds on [Collections-Review-And-Genesys-Dispatch.md](Collections-Review-And-Genesys-Dispatch.md). **Live customer dispatch is disabled and cannot be switched on by accident:** three
separate settings are all `false` in the committed configuration (`GenesysOutbound:Enabled`, `LiveCustomerDispatchEnabled`, `SuppressionEnabled`) and confirmation is refused unless all three are on.
Nothing was uploaded to Genesys during development; Genesys was only ever mocked. The `OAuth.txt` file you attached was not read into any file or log and no credential was used.
Because it was shared in chat, treat that client secret as exposed and rotate it before UAT.

## 1. Templates and review exports: what was checked

Files: five `TH_Collections_<Type>_<listId>.csv` Genesys contact-list templates and three `collections-OverdueReminder-2026-10-09-review*.csv` exports (1,077 records each). The five templates are in
`docs/Collections/genesys-templates/` and checked by tests; the review exports contain customer data and are **not** committed.

**Templates**
| Check | Result |
| --- | --- |
| Header | All five are exactly `Phone,CustomerName,Email Address,ReminderType,AmountDue,DueDate`. Matches the mapping, including the space in `Email Address`. |
| List id in each file name vs configured list | 5 of 5 match: CurrentMonth `79e5ae74…`, FollowUp `3a91c06e…`, LegalCase `41178d2a…`, LegalNotice `372d81d7…`, Overdue `d5f4d808…`. |
| **ReminderType values** | **Cannot be verified: the templates contain none.** Each holds one placeholder row `977272,,,,,` (blank ReminderType; 977272 is not a valid phone). The only evidence is the file names, which spell the types without spaces (`CurrentMonth`, `FollowUp`, `LegalCase`, `LegalNotice`, `Overdue`). The code sends the labels from your list table: `Current Month`, `Follow Up`, `Overdue`, `Legal Notice`, `Legal Case`. **Decision needed:** the exact ReminderType strings the Genesys flows/scripts expect. Change them with `Collections:GenesysOutbound:ReminderTypeLabels:<Type>`; no code change is needed. A test pins the current labels and a verifier reports any other value found in a list export. |

**Review exports vs the contact-list template** (mismatches)
| Review export column | Contact-list column | Mismatch |
| --- | --- | --- |
| `Email` | `Email Address` | renamed |
| `Amount` | `AmountDue` | renamed; export values are not two-decimal (`37797.0800000001`, 24-digit values) |
| `Stage` (`OverdueReminder`) | `ReminderType` | different vocabulary; the export has stage names, the list has labels |
| `Phone` | `Phone` | review files prefix `'` (`'+971…`) to stop spreadsheet formulas; a list needs the plain `+971…` |
| 16 other columns (RecordId, Status, Reason, VoiceEligible …) | – | internal; must not be uploaded |
The review export is an internal staff file and is not the upload format. The upload (and a new "download the contacts as uploaded" CSV per dispatch) is written by `GenesysContactTemplate` in exactly the six columns; `GenesysContactTemplate.Verify` checks any CSV (or a Genesys list export) for column names, E.164 phone, two-decimal `AmountDue`, `yyyy-MM-dd` `DueDate` and ReminderType values.

**What the 1,077 real Overdue records showed** (Company 4: 1,003; Company 32: 74; all AED; all `NeedsReview`; the three files agree except two rows that changed between runs minutes apart)
- `SourceReconciliationRequired` 1,077 (config, expected). `AmountPrecisionNeedsReview` 621. `NoValidContact` 98.
- **Precision (root cause now confirmed on real data):** 619 of the 621 deviate from a two-decimal amount by 1e-9 to 1e-14, i.e. float noise; 36 records are pure residue (`7.27E-11` …) that look like positive balances but are zero. With the new normalizer: 36 dropped as settled, 619 normalized to fils, and **2 stay flagged** because they are genuine three-decimal balances (`0.398`, `291906.651`). Also 142 real balances are below AED 1 (e.g. `0.76`); 331 are below the AED 100 default minimum.
- **Contacts:** only 339 records have a phone (334 UAE mobiles, 5 international: `+966…`, `+7916…`, accepted). 640 have an email but **no phone**, 98 have neither. The previous rule counted an email as a valid contact; for a voice campaign 738 records (69%) are not callable. Whether the phone is empty in PACT or rejected by the normalizer cannot be told from the export (it holds only the normalized value): see the "evidence needed" list.
- **Project:** `ProjectCode` is blank on all 1,077. It is hard-coded `''` in `dbo.p4AccountReceivables`, and absent in p32. The Project filter has nothing to match with the current source. Unit codes begin `TPnnn`; whether that prefix is the project is a business decision (not assumed).
- **Customer names carry the unit** in 980 of 1,077 records (`Name-601- A`, with unit code `TP130-601-A`). That string would be sent as `CustomerName` and spoken by a voice bot. Not changed; confirm what the script should say.
- **Shared phones:** 25 phone numbers are used by 2–5 *different* customers (60 records). All are held back (see 5).
- The three runs show two rows changing (a float digit; and one record whose earliest due date and amount shifted) between runs a few minutes apart: balances move, which is why revalidation precedes every send.

## 2. Payment status and the V2 procedure
- V2 reports `Amount` (remaining), `PlanAmount` (original) and `AllocatedAmount` (paid) with `PlanAmount = AllocatedAmount + Amount` (decimal(19,4)). The application now reads all three. **Unpaid** = paid 0; **PartiallyPaid** = 0 < paid < original; **Unknown** (never sendable) when the three do not add up (`SourceAmountsInconsistent`), any is missing, or the original procedures are used (they return neither plan nor paid, so every open balance stays Unknown, as in the export).
- Per-instalment "paid" is a convention: PACT has no per-instalment payment. V2 takes the tag-level paid total (invoices − ledger balances) and applies it oldest-first. Confirm that with Collections (reconciliation checklist §3).
- **Deployment package:** `docs/Collections/pact-sql/deploy/` — `00-preflight.sql`, `10-/11-deploy-p4|p32AccountReceivablesV2.sql`, `15-grant-execute.sql`, `20-/21-smoke-and-compare-*.sql`, `90-rollback.sql`, `README.md` (order, configuration, rollback). The deploy bodies equal the reviewed drafts byte-for-byte (tested). **Not executed against SQL Server by me.** The procedures belong in the database that holds the originals (the `PACTRPT` database); its name is not in the repository.
- Configuration: `CollectionsSource__PactReceivables__ProcedureSuffix=V2` (UAT first). Rollback: set it back to empty; the originals were never modified.
- Caution: V2's default `@StrictIdentity = 1` makes the whole read fail when a voucher maps to more than one Tag or unit; run reconciliation R3 first.

## 3. Reconciliation
`pact-sql/reconciliation-checklist.md`: sample cases (unpaid, partly paid, fully paid, overpaid, float residue, float noise, genuine sub-fils, multiple instalments, several units, same-date instalments, shared phones), evidence per case, totals, sign-off. `FinancialSourceValidated` remains `false`. **Still needed:** PACTRPT access (or a DBA), someone who can read the PACT ledger, masked raw `Mobile` values for blank-phone rows, and the project mapping decision.

## 4. Paid after upload

Verified against Genesys' published API reference (SDK documentation, `OutboundApi`, 2026):
| Operation | Fact |
| --- | --- |
| `PUT /api/v2/outbound/contactlists/{contactListId}/contacts/{contactId}` | "Update a contact"; body `DialerContact` with writable `callable` ("whether or not the contact can be called"); permission `outbound:contact:edit`. **Used.** |
| `DELETE …/contacts?contactIds=` | "**Only contacts that are not in use by any campaign will be deleted.**" So deletion cannot be relied on for contacts a campaign already holds. **Not used.** |
| `POST …/contacts/bulk/update`, `bulk/remove` | asynchronous jobs; completion has to be polled. Not used (a per-contact PUT is confirmed in the response). |
| `POST …/contacts` | accepts `priority`, `clearSystemData`, `doNotQueue` query flags. Not used. |

Implementation: a sweep (`SuppressionService`, Hangfire recurring `collections-genesys-suppression`, every 10 minutes by default; also `POST /api/collections/review/suppression/sweep`) re-reads current balances and
`PUT`s `callable=false` (with the original data) for every uploaded contact whose balance was **paid or changed** (amount, due date, phone). It counts a contact as suppressed only when Genesys answers
2xx with `callable:false` (or 404: the contact is gone); anything else is `Failed` (retried) or `Unconfirmed` (retried). Status per contact, counts and failures show on the dispatch page. Contacts uploaded without a returned id
(after a manual reconciliation) cannot be updated and are listed for manual follow-up. Uploaded contacts are swept for `SuppressionWindowDays` (3). The sweep is also bound to the contact's business month, so month rollover does not look like payment.

**Remaining timing limits (cannot be removed in code):**
1. A call already dialled, ringing or connected cannot be recalled.
2. Between a payment in PACT and the next sweep (up to 10 min + the PACT read time, minutes per company + Genesys rate limits) the contact stays callable.
3. A running campaign may already have pulled the contact into its queue. **Whether Genesys honours `callable=false` for such a contact is not documented in the places I could read; it must be proven on a test list** with an active test campaign (step 6 of the UAT below).
4. The Genesys client needs `outbound:contact:edit` as well as `outbound:contact:add`; check the OAuth client's role.
5. The balance is only as current as PACT; payments not yet posted cannot be suppressed.
**Live dispatch therefore stays disabled until step 6 passes.** The three-way gate above enforces it.

## 5. Repeated calls: blocked pending your decision
No acknowledgement releases them. Two kinds of record are held back (status *Excluded*) and cannot be selected, confirmed or uploaded:
- `SharedPhoneMultipleUnits`: the same phone on several units or customers (same customer with two units, or different customers). Amounts are never merged and no unit is chosen.
- `ReminderTypeOverlap`: no longer produced. Campaign types are independent, so a unit that qualifies for several reminder types today (e.g. Current Month and Legal Notice on day 14) is eligible in each; only legacy review data still carries it.
The review page shows a panel (and `GET /api/collections/review/overlaps`) listing each affected phone with customer count, unit count, records, why, reminder types and amounts. Filter by either reason to see every record.
**Decisions needed:** (a) one call per unit, one consolidated call per customer, or the first/highest priority; (b) which reminder type wins when two apply on one day; (c) whether people sharing a number are valid contacts.
In the Overdue export alone, 60 of the 339 phone-bearing records are affected.

## 6. Revalidation performance
- Source limitation (stated in `CurrentBalanceReader`): the PACT procedures accept only `@StartDate`, `@EndDate`, `@MinAmount` (V2 adds `@IncludeSettled`, `@StrictIdentity`); none selects a customer or unit and the ledger/allocation work always runs in full. A targeted read needs a new, separately reviewed procedure parameter; none is invented. The window cannot safely be narrowed because a unit's amount sums all qualifying instalments from the start date.
- Done: one read per company (not per row), both companies concurrently (tested), a **lease heartbeat** (the lease is renewed while the read runs, so a read longer than the 5-minute lease cannot let a second worker start; tested with a clock advanced 6 minutes), **lease-loss detection** (the worker stops without writing; tested), a final lease check before writing, visible progress (`Phase`: "Checking current balances…", "Uploading batch 2 of 3", "Finished") and the recorded revalidation time on the dispatch.
- **Measured here (SQLite, this machine; not PACT):** validating 250,000 source rows into 125,000 records 4.2 s; a refresh of 40,000 source rows (read + validate + save) 2.1 s; a filtered + counted + paged query over 10,000 records 110 ms. **PACT/SQL Server timings could not be measured** (no access). The only figure on record is yours from the earlier review: about 58 s for `p4AccountReceivables` with `@MinAmount = 1000`; the app budget is `CommandTimeoutSeconds + 30` (150 s) per company, so one revalidation is ≈1–2.5 minutes. The first UAT run will log "Current balances read … in N ms" and store `RevalidationMs` on the dispatch.

## 7. `callable`
`callable` is `true` only when the item is `VoiceEligible` (frozen at approval: Ready + a valid international number) **and** its phone matches E.164 at upload; the suppression PUT always sends `false`. A record that is not voice eligible when the job runs is excluded, never uploaded as non-callable. Confirmation refuses a list containing a record without a callable phone. Tests: all uploaded payloads callable and E.164; tampered eligibility excluded; empty/invalid phone yields `callable=false`; the HTTP body carries the same value. Note: `callable` is not a do-not-call or calling-hours control; those remain Genesys campaign settings.

## 8. Verification
`dotnet build src/TigerCS.slnx -c Release`: 0 warnings, 0 errors. `dotnet test`: **4,000 passed, 0 failed** (221 in the review/dispatch area, 69 added in this round: templates and real-export regressions, deploy scripts, the live gate, callable, suppression and its Genesys HTTP contract, ambiguous outcomes (fewer ids than contacts, unreadable ids, mixed batches never resent), cancel-vs-job-start race (12 randomized rounds, on a real file database with concurrent connections; sample: cancel won 11, job won 1, never both), a cancel during revalidation, lease heartbeat/loss, concurrent company reads, overlaps).
Not covered: real PACT, real Genesys, SQL Server (migrations and the filtered unique indexes are generated for SQL Server; tests run SQLite).

## Decisions and access still outstanding
| Needed from | Item |
| --- | --- |
| Business | Exact `ReminderType` strings (templates are blank); repeated-call policy (5); Legal Notice / Legal Case customer contact (`LegalNoticeExportEnabled`, `LegalCaseDispatchApproved`); what `CustomerName` should say (unit suffix); project definition (`TPnnn` prefix?); minimum amount below AED 1 balances |
| DBA / PACT | PACTRPT access or a DBA to run `deploy/` and the reconciliation; sample judgement from someone who reads the ledger; masked raw `Mobile` values |
| Genesys admin | A **test** contact list per type and a test campaign; OAuth client with `outbound:contact:add` + `edit`; proof that `callable=false` stops a queued contact; rotated credentials |
| Engineering (later) | Targeted PACT read (new reviewed procedure parameter); delivery-result feedback from Genesys into dispatch items |

## Deployment and UAT, in order
1. Merge/deploy the branch; apply the migrations: `dotnet ef database update --project src/TigerCS.Infrastructure` or run `AddCollectionsReviewAndGenesysDispatch.sql` then `AddDispatchSuppressionAndVoiceEligibility.sql` (both idempotent, in the repository root; the second drops the unreleased `AcknowledgedSharedPhoneCalls` column — nothing else is lost).
2. UAT database: `pact-sql/deploy/` steps 1–5; set `ProcedureSuffix=V2`; Refresh review data. Expect Unpaid/PartiallyPaid statuses; no Unknown left except inconsistent rows.
3. Reconciliation (`reconciliation-checklist.md`); sign-off; then set `FinancialSourceValidated`, `ValidatedProcedureSuffix`, `ReconciliationReference` **in UAT only**.
4. In UAT set `Collections__GenesysOutbound__ContactListIds__<Type>` to **test** lists, store `ClientId`/`ClientSecret` as secrets, and enable `Enabled`, `SuppressionEnabled`, `LiveCustomerDispatchEnabled`. Use test customers/phones only.
5. Approve a handful on a scheduled day; confirm the uploaded contacts equal the "download contacts" CSV and the template; verify list routing, `callable`, ReminderType.
6. **Suppression proof:** with a test campaign *running* on the test list, mark one uploaded test customer paid; run the sweep; confirm Genesys shows `callable=false` and that no call is placed to that contact, including for a contact already queued. Record the evidence. Only then may production lists and `LiveCustomerDispatchEnabled` be considered.
7. Also test: double click, two tabs, a paid-before-send record, a changed amount, a simulated timeout and its reconciliation, cancel of a queued dispatch.
