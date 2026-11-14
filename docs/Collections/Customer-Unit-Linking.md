# Customer and unit linking (Payment tab, phone lookup, New Ticket)

One service, `CustomerUnitLinkService` (`src/TigerCS.Application/Modules/Collections/Services/`), links a customer to their units and to the PACT figures of **each unit**.
A phone number only *finds the customer*; money is never looked up by phone.

## Flow

1. **Normalise the phone** (`CollectionsContactNormalizer.NormalizePhone`, E.164; `0501234567`, `971501234567`, `+971501234567` are one number). CRM is asked with the number as typed (as the New Ticket lookup always did), PACT with the normalised number.
2. **CRM first.** `GetBuyerByPhone` gives the customer and their units. Only a **buyer's Sold or Contract lead that is not cancelled** counts (`CrmSaleEligibility`; the status *name* decides - see "Status definitions" below). Several CRM customers on one number are returned as separate candidates, never collapsed to the first.
3. **Each CRM unit -> PACT.** CRM sends only `projectId` + unit number. The verified `dbo.CollectionsCrmProjectMap` (V011) gives the PACT project code (`TP140`), so the PACT unit code is `TP140-909`. The company comes from the mapping row, else from `dbo.CollectionsTowers` by tower number. Never from a display name.
   * no mapping row -> `MatchFailed / ProjectMappingMissing` (no amounts are looked up, none are implied)
   * several codes for one CRM project -> `ProjectMappingAmbiguous`
   * the tower exists in both companies and the mapping does not name one -> `CompanyMappingAmbiguous` (nothing is chosen)
4. **Financials = the PACT account of that company + unit** (the same snapshot read as the Receivables page and the unit Payment Summary, `usp_Collections_GetUnitReceivables`): every unpaid instalment of that unit, classified with the one current-Dubai-month rule (`CollectionsDueMonth`): Overdue before the month, Due inside it, later months *Not yet due*. The Payment tab's figures are described in [Unit-Payment-Summary.md](Unit-Payment-Summary.md). It does not matter whether the CRM phone exists in PACT.
5. **Contact.** CRM name/mobile/email first; PACT fills a missing field; two different people are never merged (`ContactSourceConflict` -> review); several CRM owners of the unit -> `CrmCustomerAmbiguous`; CRM owner data naming a different customer -> `CrmOwnershipConflict`. A missing mobile never blocks the match.
6. **Only when CRM holds no eligible customer** is PACT searched by phone (`v1/contracts/{mobile}`): companies 4/32 go through the same unit read, restricted to that contract's tenant (a former tenant of the same unit is not mixed in); **company 7 (Leasing)** is listed per contract. Ended contracts (`contractEndDate` < today) and cancelled apartment codes (`*`) are excluded.
7. **More than one customer / unit / contract = a list with `selectionRequired`.** The first is never taken. `selectionId` is `crm:{crmUnitId}` or `pact:{company}:{tenant}:{unitId}:{contract}`.

## Financial states (per unit)

| `financialStatus` | Meaning |
| --- | --- |
| `Available` | Amounts due before the end of the current Dubai month (Overdue: before its first day; Due: inside it); `due`, `overdue`, `total`. |
| `NoDues` | **Confirmed zero**: PACT holds the unit and nothing is due. |
| `NoFinancialData` | Nothing usable (`PactHoldsNoRecord`, `CompanySnapshotNotLoaded`, `LeasingReceivablesSourceMissing`, `CompanyNotSupported`). **Not zero**: amounts are null. |
| `MatchFailed` | Failed or ambiguous match (`ProjectMappingMissing/Ambiguous`, `CompanyMappingAmbiguous`, `UnitCode…`, `CompanyMissing`, several PACT accounts). No amounts. |
| `SourceError` | A source could not be read (`PactUnavailable`, `ProjectMappingUnreadable`). Retry; no amounts. |

### Comparing the screens (what must match and what need not)

For the **same unit and the same scope** (all months, current Dubai month rule), **Due and Overdue must be identical** on Receivables, Campaigns and the Payment tab (`due` / `overdue`, and the cards *Due This Month* / *Overdue*). **Total** in Collections is `Due + Overdue`.
The Payment tab's **Remaining Balance is every unpaid payment, future ones included** (`payments.remainingBalance`, from the schedule table), so it is **not** required to equal Due + Overdue; it equals them only when nothing is due after this month. Cards and the schedule table are computed from the same rows (Due card = sum of the *Due* rows, Overdue card = sum of the *Overdue* rows, Remaining = sum of all rows). *Total Amount* and *Paid Amount* are `Unavailable` (the snapshot holds only unpaid remainders). Example: Due 4,000, Overdue 1,000, Remaining 16,500 is consistent (11,500 is not yet due).

## Entry points (all reuse the service)

| Caller | Route / code |
| --- | --- |
| Customer Details -> **Payment tab** | `CustomerPaymentPanelLoader.LoadAsync` -> `GET /api/collections/customers/crm/{crmCustomerId}/units` first; the per-account / EDSM views are used only when they show real figures, otherwise the unit diagnosis replaces the generic "No payment figures" message. |
| `/Customers/Payments` (payments before a ticket) | `LoadLookupAsync` -> same route (CRM keys); PACT keys fall back to `GET /api/collections/customers/lookup` filtered to that tenant when EDSM shows no figures (Leasing). |
| **New Ticket** (property + review steps) | `PaymentPanelOptions.PreferredCrmUnitId` = the selected CRM unit; typed phone as `CrmLookupPhone`. The panel shows name, mobile, email, tower and apartment of the selected unit. |
| Phone search / API | `GET /api/collections/customers/lookup?phone=&selection=` |
| Unit code (unchanged) | `GET /api/collections/units/payment-summary`, `…/leasing/payment-summary`, all Genesys routes - not modified. |

## Root cause of the reported "No payment figures for this customer" (CRM 498397, Al Ghaf Tower 909)

Traced through the code (the real CRM/PACT/API could not be reached from this environment, so the trace is by reading the code path, not by observing the live response):

1. `CustomerProfile` -> `CustomerPaymentPanelLoader.LoadAsync("crm:498397", 498397)` -> `GET /api/collections/customers/498397/outstanding`. The per-account source is `UnavailableCollectionsFinancialSource` unless `CollectionsSource:Provider` is set (default `Unavailable`; `Fixture` is refused outside Development/Testing) -> 503 `FinanceUnavailable` -> panel state `Unavailable`.
2. The loader then asks EDSM: `GET /api/collections/customers/by-key/crm:498397/payment-summary` -> `CollectionsPaymentSummaryAppService.ResolveAsync`. EDSM is keyed by **PACT companyID + tenantID**. A `crm:` customer has no `ExternalSource = Pact` / tenant id, so the method returns at once, **before any PACT call**, with `NotMappedDetail = "…identified by Tiger CRM… no verified mapping from a CRM customer to a PACT tenant exists"` -> `MappingStatus = NotMapped` -> `_CustomerPaymentTab.cshtml` renders `No payment figures for this customer.`

So the verified cause is **a missing CRM-customer -> PACT identity mapping in the only route the Payment tab used** (it is not phone discovery - the phone is never even sent to PACT for a `crm:` key - and it is not a missing snapshot: the unit's PACT receivables were never consulted). The old tab could therefore *never* show figures for a CRM-only customer unless a ticket had linked them to a PACT tenant. After this change the tab asks for the **unit** first. Whether Al Ghaf Tower 909 then shows figures depends on two facts that must be checked against the real systems: (a) the CRM project "Al Ghaf Tower" has a row in `CollectionsCrmProjectMap` (or the CRM owner feed supplies it) and (b) PACT holds `TP<tower>-909` in the loaded snapshot. Missing (a) now reads `ProjectMappingMissing`, missing (b) `PactHoldsNoRecord`, a confirmed zero `NoDues` - all distinguishable.

## Status definitions

The CRM lead-status numbers are **confirmed by the CRM owners**: Sold = 8, Contract = 9, Approved = 4 (not a sale), Cancelled = 6. Eligibility still uses the **status name** CRM sends (Sold / Contract, never containing "cancel"); `CollectionsSource:CrmOwners:EligibleLeadStatuses` defaults to `[8, 9]` and is a fallback only for a row with no name. Buyer `customerType` = 1.

## Leasing (company 7) - what exists

* Discovery by phone: yes (`v1/contracts/{mobile}`, `companyID = 7`), current contracts only.
* Financial source: **none**. The snapshot, its tables and refresh procedures (`p4AccountReceivablesV2`, `p32AccountReceivablesV2` on PACTRPT via `[10.10.10.94]`) are hard-wired to companies 4 and 32; the repository contains no company-7 procedure, so none is invented. Leasing contracts are returned as `NoFinancialData / LeasingReceivablesSourceMissing` with that exact text. Needed: a PACTRPT procedure for company 7 with the same columns (tenant, unit, contract, voucher, due date, remaining/original/paid amount, status) plus snapshot support.
* Cancelled contracts: PACT's contract lookup carries no cancellation or status field (`unitStatus` is not interpreted - its meaning is unpublished). Only an ended contract and a cancelled apartment code (`*`) can be excluded today.


## CRM contact and tower names (V012) - what was wrong and what the rules are now

Traced by reading the code path (the real CRM / PACT / production database cannot be reached from the build environment; `database/collections-receivables/tools/diagnose_crm_unit.sql` tells which of these causes hit a given unit).

**Why a tower showed only its number (TP119 -> "119").** Every screen took a tower's name from `dbo.CollectionsTowers` alone. A tower PACT holds that has no row there (V001: "Tower 119 is intentionally NOT seeded"), or whose row only repeats the number, rendered its number. The real name is what CRM calls the tower's project (`projectName` of the owner feed). It is now resolved in ONE place, `dbo.fn_Collections_TowerNames` (V012): `CollectionsTowers` when its name is a real name (not blank, not just `119` / `TP119`), else the single project name CRM holds for **that company's** tower; two different CRM names = no name. Nothing is typed into the application. Tables, the picker, the unit details and the review export all read it. `tools/repair_tower_names.sql` (dry run by default, re-runnable) writes the missing rows / names into `CollectionsTowers` so the tower can also be chosen in the picker.

**Why a CRM owner's contact did not reach a unit.** An owner got its company only from `CollectionsTowers`, and only when the tower number belonged to exactly ONE company. A tower missing from that table or present under both companies left the owner **without a company**, and an owner without a company is never joined to a unit: the page silently showed the (possibly empty) PACT contact. Other ways the contact can be lost, all now visible: the feed row had no PACT project code (counted as `NoProjectCode`, filled from the verified `CollectionsCrmProjectMap` when it has exactly one code for the project), a mobile that was valid but written in a form the normaliser did not accept (`966508650505` / Arabic-Indic digits were rejected; +966 is now kept as +966), and a CRM service problem that was shown as "No mobile".

**The company of an owner (V012).** In this order: (1) the verified project map names the company (`Map`); (2) `CollectionsTowers` has the tower in exactly one company (`Tower`); (3) exactly ONE company's PACT snapshot holds this **exact unit code** (`Snapshot`, recomputed after every receivables refresh by `usp_Collections_ResolveCrmOwnerCompanies`); (4) both companies hold it = **`Ambiguous`**: the sale is tied to neither, the unit is flagged `CrmCompanyAmbiguous` (needs review) and no CRM contact is used. A tower number alone never decides when it exists in two companies.

**Contact rules (unchanged in meaning, now applied everywhere through `CollectionsUnitLinkService` / `CollectionsContactLinker`).** CRM name / mobile / e-mail first; PACT completes only a field CRM lacks, and only when nothing contradicts CRM (different phones, or different e-mails when no phone confirms) - otherwise `ContactSourceConflict` and CRM alone is shown; several CRM buyers = `CrmCustomerAmbiguous`, nobody chosen. The same service feeds Receivables, Campaigns, the Payment tab / Payment Summary, both exports and the review records behind the Genesys lists (`ReviewRecordBuilder`); the contacts are re-linked from the CURRENT CRM data when a dispatch revalidates, so a stale approval copy is never trusted. CRM is read in bulk (one statement per request), never per row.

**Campaign mobile = CRM only.** For every Collections Campaign (preview, eligibility, customer search, Genesys export, review CSV, send, and the revalidation before an upload) the MOBILE comes from the CRM buyer of the unit and nowhere else (`CollectionsContactLinker.LinkForCampaign`). The buyer is found by the unit (CRM owner snapshot + verified project mapping + Sold 8 / Contract 9, one bulk read, no per-row CRM call), never by a PACT phone. The CRM mobile is normalised and validated (E.164); the PACT phone is **never** a fallback. A PACT phone that is missing or invalid therefore does not hold a unit back when CRM has a valid mobile; a unit with no eligible CRM buyer, a CRM buyer without a valid mobile, several buyers (`CrmCustomerAmbiguous`, nobody chosen) or an unusable CRM feed (`CrmUnavailable`) is **Needs review** (`NoValidContact`) and is never sent. PACT remains the financial source, and name / e-mail keep the rules above. (Receivables and Payment Summary pages keep the rule above unchanged.) Note: with CRM linking switched off, no campaign unit has a mobile.

**A CRM service problem is not "CRM has no mobile".** `CrmAvailability`: `Disabled` (linking switched off: PACT contacts only, nothing flagged), `Ready`, `NotLoaded`, `Stale` (last successful load older than `CollectionsSource:CrmOwners:MaxAgeMinutes`, default 360) and `ReadFailed`. The last three block: every unit gets the review reason `CrmUnavailable`, the pages show a banner, **no Genesys list can be exported or dispatched** until CRM is back (the internal review CSV stays available). A failed latest refresh with recent data is a warning only.

**Order.** Receivables, Campaigns and the tower picker list the newest tower first: the tower **number as an integer, descending** (TP140, TP132, TP131 ... TP119; never the text, never the name; a non-numeric tower after the numeric ones). Inside a tower: unit number ascending (numeric before lettered), then the unit code, company, customer, unit id. The order is decided over the WHOLE filtered result before paging (SQL `ROW_NUMBER` for Receivables, the full in-memory result for Campaigns), so pages never repeat or skip a unit. The application (`CollectionsTowerOrder`) and SQL implement the same order; tests compare them.


## Round 2: per-unit Payment Summary, project mapping evidence, contact states, cancelled units

**What could and could not be verified.** No real CRM or PACT was reachable from the build environment. Everything below is proven with synthetic fakes and a throw-away SQL Server; none of it proves what production CRM/PACT return for BEN ISRAELI (project 94 / unit 3302) or TP140-407 (lead 307092). Use the read-only checks at the end.

### Payment Summary (Due | Outstanding | Paid | Fines | Total + Payment Details)
* Source: PACT's EDSM payment summary and transactions (the "Pact Payment" source), never the Due/Overdue snapshot. The snapshot cards stay, relabelled "unpaid instalments only".
* EDSM has no unit scope (per company + tenant). `CollectionsUnitStatementService.Resolve` therefore shows amounts only when PACT's contract list (`v1/contracts/{mobile}`, all units of the tenant) proves the (company, tenant) account holds **only** the selected unit. Another unit, parking, a cancelled (`*`) or ended contract, a contract without a unit code, or an incomplete PACT answer withholds the amounts with the reason (`NotExclusive`, `Unverified`, `ContractEnded`, `IdentityMismatch`, `Ambiguous`, `UnitNotFound`, ...). Missing is never zero.
* Fines: owned companies report only fines above zero, so an empty value is shown as 0; rented companies never compute fines ("Not computed").
* Consequence to know: a customer who owns several units sees the withheld notice instead of the statement, because EDSM cannot split the account. A unit-scoped PACT route would lift this.

### CRM project -> PACT mapping
* The mapping is consumed by `CustomerUnitLinkService` through `usp_Collections_GetCrmProjectMap`: manual rows (`CollectionsCrmProjectMap`) first, then the project/code pairs of the CRM owner feed. In production the owner feed is switched off, so nothing populated it: a project without a manual row failed with "CRM project has no verified PACT tower mapping."
* New fallback (per unit, no names, nothing hardcoded): the CRM unit number must equal the apartment of exactly one active, non-cancelled PACT contract of the SAME customer; that contract's unit code gives tower and company. Several matches -> `ProjectMappingAmbiguous`; none -> `ProjectMappingMissing` with a reason. Each observation is stored (V013) for review.
* `94 -> Tower 125 -> Company 4` could **not** be verified from the repository or any available source and is not applied. Run `tools/review_crm_project_map.sql` after a few lookups; it lists project 94 as ReadyToPromote only if the customers' own contracts prove one tower/company.

### Contacts
* Root cause of the empty contact on TP140-407: `CollectionsSource:CrmOwners:Enabled=false` (CRM has not published `GetUnitOwners`), so no unit is linked to a CRM owner and the page showed PACT's empty contact under the wording "No mobile or email in CRM or PACT". That wording was false.
* Now: **switched off** ("CRM linking is switched off, so CRM was not checked"), **owner not linked** ("no CRM owner is linked to this unit"), **ambiguous owner / company**, **CRM unavailable** (service problem) and **linked but empty** ("in CRM or PACT") are different messages; the page also shows a notice when the feed is switched off. CRM stays first; PACT only fills missing fields; different people are never merged.

### Cancelled units (`*` anywhere in the PACT unit code, including `**`)
Excluded before matching, aggregation and paging in SQL (`R002` refresh, `R004` procedures), in `ReviewRecordBuilder`, from the CRM unit candidates and PACT-evidence matching, and from the Payment statement. The mark is never stripped to match an active unit. `DispatchService` re-checks the prepared items before any Genesys upload (reason "Cancelled unit (code contains *)"), and again when batches are planned. A cancelled sibling unit on the same EDSM account withholds the statement (its amounts cannot be separated).

### Read-only checks for the two failing units (run in production, nothing is written)
1. `GET /api/collections/crm-owners/status` - is the CRM owner feed enabled/loaded? (If Disabled, TP140-407 cannot link; enable it only after CRM publishes `GetUnitOwners`.)
2. Mapping 94: open the BEN ISRAELI Payment tab once, then run `database/collections-receivables/tools/review_crm_project_map.sql` (preview). Compare any ReadyToPromote row with CRM `tblProjects` 94 and PACT tower 125 / company 4 before `@Apply = 1`.
3. TP140-407: in CRM run `SELECT TOP 5 * FROM tblLeads WHERE ID = 307092` and the buyer's mobile/email in `tblCustomers`; in PACT call `v1/contracts/971529586320` and confirm the unit code `TP140-407` and its company/tenant.

## Round 3: unit-based statement, contact trace, and what is still outstanding

> **Superseded by Round 4 below:** points 1 and 3 of this section (identity from the open-balance snapshot, exclusivity from open siblings) were insufficient - fully paid units and paid siblings were invisible. Round 4 replaces them with the unit/account directory and a complete-evidence rule.

### Code completed (tested with fakes and a throw-away SQL Server; NOT verified against real CRM / PACT)
1. **Account from the unit, no phone gate.** `CollectionsUnitStatementService` reads the exact unit code from the existing PACT unit data (`usp_CollectionsRcv_GetUnit`) and takes **CompanyId, TenantId and UnitId** from that match. PACT's mobile is not needed: EDSM's `Mobile` parameter is required by the route but, for owned companies and Paid/Due/Outstanding, unused (contract section 5.2), so a unit with no PACT/CRM mobile sends `0` (`MobileNotUsed`; a rented company without a mobile is refused). `contracts/{mobile}` is now optional extra proof: when a phone is known it can reveal other units of the account (including fully paid ones); when PACT is down the statement still works and says it was confirmed from the snapshot only.
2. **Statement** = EDSM payment-summary (Due, Outstanding, Paid, Fines, Total as the source states them) + payment-transactions (every Paid / Due / Outstanding row: Date, Status, Amount, Cheque Number), same field meanings as before.
3. **Never another unit's money.** EDSM aggregates per (company, tenant) and has **no unit parameter** (`EDSM_Collections_Contract.md` s.1, 2.3, 3.2, 5.2). `usp_CollectionsRcv_GetUnit` now also returns the other units of the same account that have an open balance (result set 5). If any exist, or the contract list shows another contract on the tenant, **no amount is shown** and the page names the limitation and the other units. Separate accounts each read only their own tenant (tested).
4. **Project mapping without guessing.** Order: verified `CollectionsCrmProjectMap` row -> the `projectCode` CRM publishes with the unit (`CrmBuyerUnitDto.ProjectCode`, optional) -> PACT-contract evidence when a phone is known (recorded, never promoted automatically) -> `ProjectMappingMissing`. The company then comes from the exact unit in PACT's data.
5. **Contacts.** Traced end to end against real SQL: CRM feed row -> `CollectionsCrmOwnersRefreshService.ToRow` (`projectCode-unitNumber`) -> `usp_Collections_StoreCrmUnitOwners` -> company from the exact PACT unit (V012) -> `usp_Collections_PublishCrmUnitOwners` -> `fn_Collections_CrmUnitLinks` -> Receivables/Campaigns/Payment. With a blank PACT mobile the CRM name, mobile and e-mail reach the page (`Tp140_407_Lead307092_IsTraced...`). A missing PACT mobile blocks neither linking nor the statement. The contact is lost in production only because the feed never ran (`CollectionsSource:CrmOwners:Enabled=false`, `GetUnitOwners` unpublished) - this is inferred from the repository, not observed.
6. **Cancelled units.** Any `*` in a PACT unit code is excluded before matching, aggregation, pagination and dispatch (snapshot SQL, unit read, tenant-unit list, CRM candidates, statement, review builder, dispatch revalidation); the mark is never stripped.

### Live verification still required (nothing below has been run against real systems)
* EDSM accepts `Mobile=0` for owned companies' payment-transactions (if it rejects it, the route needs a real mobile - then PACT/CRM must supply one for that unit).
* `p4tenantStat` / `p32tenantStat`: whether a TenantId is one unit or one customer. If TenantId is per unit, every unit gets its own statement; if per customer, a multi-unit customer sees the limitation notice. `probe_pact_tenant_granularity.sql` answers it.
* The statement figures match the CRM Pact Payment screen for the same unit (reference: lead 293168 - 0.00 / 889,393.44 / 594,467.56 / 0 / 1,483,861.00).
* `GET /api/collections/crm-owners/status` on the deployed API (is the feed enabled / loaded?).
* Project 94 / VOLGA / unit 3302: run `probe_crm_pact_payment.sql` sections 4-6 and review the evidence script; `94 -> Tower 125 -> Company 4` is not applied.
* A unit with **nothing open** is not in the snapshot, so its account cannot be found from unit data; it needs a phone (contract list) to resolve.

### External requirements (exact)
1. **CRM `GetUnitOwners`** (contract: `CRM-GetUnitOwners-Contract.md`) must be published and `CollectionsSource:CrmOwners:Enabled` switched on, or contacts stay PACT-only. To implement it against the real schema only these are needed: the EF classes (or `CRM-Schema-Discovery.sql` section 1) of **`tblCustomers`** (English name, Arabic name, e-mail columns; `MobileNumber` is confirmed), **`tblLead`/`tblLeads`** (the `ProjectCode` and `UnitNumber` columns the legacy EDSM query reads on `tblLead`, and their EF names) and **`tblProjects`** (any code column). Already confirmed from the repository: `tblLeadCustomers(LeadID, CustomerID, CustomerType, UnitID)` ⨝ `tblLeads(ID, Status)` ⨝ `tblUnits(ID, Number, ProjectID)` ⨝ `tblProjects(ID, Name, ArabicName)`.
2. **Optional but removes the mapping problem:** CRM `GetBuyerByPhone` returning `projectCode` (the TP### CRM holds) per unit.
3. If a unit-level payment statement is required for customers whose tenant covers several units: a PACT/EDSM route or procedure that takes a unit or contract id (the CRM "Pact Payment" source, if it has one - its controller action / query is not in this repository).

Asterisk-marked PACT units remain excluded everywhere.


## Round 4: payment works for fully paid units; scope must be PROVEN

* **Identity from a unit/account directory, not the open-balance snapshot.** `usp_CollectionsRcv_Refresh` now also publishes `CollectionsRcvAccountUnit`: every (tenant, unit) PACT's receivables procedure returned, **settled (fully paid) units and cancelled `*` codes included**. `usp_CollectionsRcv_GetUnitAccounts` returns the exact unit's (CompanyId, TenantId, UnitId) and every other unit of the same account. A fully paid unit with no PACT mobile is resolved from it (tested on real SQL and end to end).
* **Exclusivity needs COMPLETE evidence.** EDSM's Paid / Due / Outstanding / Total cover the whole (company, tenant) account, so a fully paid sibling contributes to Paid and Total. Any other unit in the directory (open, fully paid or cancelled) or in PACT's contract list withholds the statement. When no other unit is known the statement is shown **only if** the evidence is complete: PACT's contract list answered for every phone, or the directory source is accepted as complete via `Collections:ReceivablesSnapshot:AccountDirectoryComplete` (**default false**; never switch it on from sample comparisons alone - see Round 5). Otherwise the status is `ScopeUnproven` and the refusal names the missing query.
* **Exact missing source.** A PACT query that lists every unit/contract of a (CompanyId, TenantId) account, fully paid ones included, without the customer's mobile - e.g. a tenant-to-units procedure next to `p4tenantStat`/`p32tenantStat`, or an EDSM route taking `tenantID` + `companyID`. `v1/contracts/{mobile}` needs a mobile; the receivables procedures omit units without a payment plan / ledger row.
* **Production consequence until that is resolved:** with the default configuration a statement appears only when a phone is known and PACT's contract list answers completely and shows the account holds just that unit; otherwise the page says `ScopeUnproven` instead of showing an account total as the unit's.
* `Mobile=0` for EDSM payment-transactions of owned companies remains **explicitly unverified** against real EDSM.
* Deploy: re-run `R001` (new table) -> `R002` -> `R004`; the directory fills at the next refresh of each company. `R004` no longer returns the open-only tenant-unit list added in the previous round.
* Minimal CRM schema for `GetUnitOwners`: `CRM-GetUnitOwners-Schema-Requirements.md`.


## Round 5: published CRM owner feed, lead statuses, and the Payment condition (unchanged)

* **CRM `GetUnitOwners` is published and tested by the CRM owners** (`success=true`, `total=10086`). Confirmed schema and mapping: `CRM-GetUnitOwners-Schema-Requirements.md`. No schema is outstanding.
* **Lead statuses (CRM-confirmed):** Sold = 8, Contract = 9 (eligible); Approved = 4 and Cancelled = 6 (never owners). `EligibleLeadStatuses` now defaults to `[8, 9]` (code, `appsettings.json`, docs). Earlier text that read 4 as Contract was wrong. Eligibility still goes by the status name when CRM sends one.
* **The feed is enabled in `appsettings.json` (`CollectionsSource:CrmOwners:Enabled = true`).** If the feed cannot be read the pages say CRM is unavailable and Genesys exports stay blocked; set it to `false` to switch CRM linking off (the pages then say CRM was not checked). **Ingestion has not been verified in this repository's environment** - it must be verified after deployment: `GET /api/collections/crm-owners/status` (fetched should equal the CRM `total`, 10086 at the time of writing; stored + rejected-by-reason must add up) and `tools/verify_crm_owner_ingestion.sql` (only statuses 8/9 stored, no starred keys, how many keys meet PACT's unit codes, the TP140-407 row), then `tools/diagnose_crm_unit.sql` for any unit that still shows no contact.
* **Payment remains conditional (`Collections:ReceivablesSnapshot:AccountDirectoryComplete = false`).** Identity for a unit with no PACT mobile - including a fully paid unit - is supported (company, tenant and unit id come from the PACT unit/account directory). Its statement, however, is shown only when complete account-scope evidence exists, because EDSM's Paid / Due / Outstanding / Total cover the whole (company, tenant) account. With the default setting that means PACT's contract list must answer completely for the customer's phone(s) and show the account holds only that unit; otherwise the page says `ScopeUnproven`. **Do not enable `AccountDirectoryComplete` on the strength of a few sample comparisons**: enable it only once it is confirmed that the directory source - PACT's receivables procedure output - includes every unit and contract of each account (it omits units without a payment plan or ledger row unless PACT confirms otherwise). The authoritative fix is the PACT query named above (every unit/contract of a (CompanyId, TenantId) account, fully paid ones included, without a mobile). `verify_account_directory.sql` only produces samples and is not proof.


## Round 6: Campaigns show exactly Receivables' units; the Payment tab shows the statement

* **Campaigns = Receivables minus the details.** For identical company, tower, date, search and minimum-amount filters, Campaigns list the same units, contacts (CRM first), tower names, order and Due / Overdue / Total as Receivables (proven on real SQL for four stages x eleven filter sets, `Campaigns_And_Receivables_ListTheSameUnitsContactsTowerNamesOrderAndFigures_...`). Campaigns have no View Details and no expanded instalment table. **Eligibility no longer removes anything from the list**: a unit the stage rule does not make eligible stays listed with its real balances and the status `NotEligible` (`NoQualifyingBalance` / `BelowThreshold`); it is never sent. The review CSV contains every listed unit; the Genesys CSV only the Ready ones, and is refused while any eligible unit still needs review. A unit whose only rows lie later this month is now listed (as Receivables lists it).
* **Payment tab.** When the statement is available it replaces the Due/Overdue snapshot cards (they stay only when the statement is withheld). Column "Payment Status" is the source list. See `Payment-Test-Guide.md` for the exact test steps.
