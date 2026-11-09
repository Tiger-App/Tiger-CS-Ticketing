# PACT Due & Overdue Customers

> **Update:** the page now reads a **local snapshot** refreshed by a background job and is filtered by **Tower + From + To** (no company
> selector; `towerId`, `dateFrom`, `dateTo` on the API). The direct-read description below remains valid for the monthly Due/Overdue
> rules. (The direct-PACT `UseLocalSnapshot=false` mode was removed with the rebuilt snapshot.) See [Receivables-Snapshot.md](Receivables-Snapshot.md).

TigerCS lists PACT apartments with a positive outstanding instalment that is Due in, or OverDue before, a selected reporting month, even if they have no TigerCS ticket or customer-directory record. The supplied report procedures cover company **4 (Dubai)** and **32 (Sharjah)** only. This feature adds no Genesys route, reminder send, next-payment calculation, database migration or background job.

## UI and API

- Navigation: **Collections**, `/Collections/Receivables`.
- Authenticated staff API: `GET /api/collections/receivables/customers?companyId=4&status=all&search=3001&page=1&pageSize=25`.
- Existing Collections financial-read authorization is enforced before any external read; the central System Administrator override applies. Reporting User alone is insufficient.
- `companyId`: omitted, `4`, or `32`; `status`: `all` (default), `due`, or `overdue`. Search matches name, tenant ID, mobile, email or unit. Page size is 1–100, default 25.
- One customer per `(CompanyID, TenantID)`, with every returned unit/instalment under that customer. Equal tenant IDs across companies are separate accounts; there is no CRM identity guess.
- Reporting month: optional `year` + `month` (both or neither; default = current Dubai month). **Due** and **OverDue** follow the monthly rule below. Amounts <= 0 are excluded. `all` is the union, not a requirement to have both.
- Count metrics reflect the selected filters before pagination. Pagination follows customer grouping, with overdue customers and oldest dates first.
- Response fields include company, tenant ID, name, mobile, email, units, dates, overdue days, due/overdue amounts, source read time, configured currency and whether legacy exclusions were applied.
- Disabled or failed sources return 503, never an empty/partial success. Invalid filters return 400, unauthorized callers 401/403. The UI has corresponding error/empty/loading states.

## Direct SQL source and amount limitation

`PactSqlReceivablesSource` calls `dbo.p4AccountReceivables` and `dbo.p32AccountReceivables` in the configured `PACTRPT` database. It passes `@StartDate` from `CollectionsSource:PactReceivables:StartDate` (default `2026-01-01`; the procedures filter only the final `DueDate`), an inclusive SQL datetime `@EndDate` at the end of the reporting month's last day, and `@MinAmount = 0`. Positive balances below 1 AED therefore remain visible. Stored-procedure names and parameters are not caller-controlled. There is a shared request deadline and a source-row cap; either failure rejects the complete response.

The supplied definitions calculate `Amount` from their payment allocation as `abs(b.PlanFutureAmount - b.NewFutureAmount - PlanFutureAmount)`. Their final invoice-to-schedule join is by tenant tag alone. Several returned rows on one date may therefore be repeated allocations or legitimate instalments. The application preserves all rows, without `DISTINCT`. A due/overdue bucket containing multiple rows on a date has a **null total** and `AmountStatus = NeedsReview`; the UI displays **Review needed** and exposes the rows. An unambiguous bucket is summed as provided. This is a conservative report of those procedures, not independent proof that the underlying allocation is correct. Unit/voucher associations remain exactly as returned by PACT.

PACT returns no currency field. `Currency` defaults to AED and is marked `CurrencySource = Configured`; confirm this during environment setup.

## Apartment list, filter and amounts

- **Only business filter:** an apartment is listed when its Due amount > 0 **or** its Overdue amount > 0. There is no company, project, contract-expiry/status or fixed-date restriction (the former `2025-11-01` bound is gone); expired contracts stay visible while they owe money. The company dropdown is an optional user filter only.
- **One row per apartment** = company + customer + unit (`UnitId`/`UnitCode`).
- **Monthly definitions** (from EDSM `ReportServices.GetAccountReceivableTransactionsAsync(year, month)`, `ReceivablesTransactionTypeEnum` OverDue = 1, Due = 2): `targetFromDate` = first day of the month, `targetToDate` = last day. **OverDue** = `DueDate < targetFromDate`; **Due** = `targetFromDate <= DueDate <= targetToDate`. `DueAmount` = Σ `Amount` of Due rows; `OverdueAmount` = Σ of OverDue rows. The month's later days belong to Due even though they are still in the future.
- The two buckets are disjoint by date, so they cannot overlap; `TotalAmount` = Due + Overdue, and is null (`NeedsReview`) if either bucket has several rows on one date.
- TigerCS reads each report with `@EndDate` = end of the last day of the month, so in-month later instalments are included. EDSM itself requests 2022-01-01 – 2031-08-01 and leaves rows after the month as type 7 (Outstanding), which are neither Due nor OverDue; they are not listed here. **Unverified:** that the procedures treat `@EndDate` as an inclusive DueDate cut-off with no other effect on `Amount`.
- **Three separate concepts per instalment:** `ReceivablesType` (Due / OverDue, relative to the reporting month), `DueTiming` (Due Today / Overdue / Upcoming, relative to today's Dubai date) and `PaymentStatus`. An instalment can be Upcoming by calendar date and still be in the month's DueAmount; one due earlier in the current month is Due by type but Overdue by timing.
- The type is assigned in EDSM application code (`TypeId` starts as Outstanding for every row). It is not a procedure parameter or filter and is not mapped from the PACT `Status` column; the enum is not used for `PaymentStatus`.
- **Classification precedence in EDSM:** rows are first set to Handover Issue (TP119/121/122/123 after cut-off dates, due on or before month end), then Legal (CRM legal-case units), then ChequesOverDue / ChequesDue (`GULF_CUSTOMERS`), and only the remaining rows become OverDue/Due. Those categories therefore *remove* amounts from the EDSM report's OverDue and Due columns. Here none of them is applied (requirement: no additional exclusions), so the TigerCS Due/Overdue amounts equal EDSM's Due+OverDue **plus** the Handover, Legal and cheque rows with the same dates. The EDSM report's `*` name and `SEPCIAL_CASES` removal and its booking/down-payment removal (only a `//TODO` there) are likewise not applied.
- The EDSM comment "Map & Merge same UnitCode, DueDate" is followed by a plain `Select`; no merge or de-duplication occurs, so none is assumed. Rows are preserved and ambiguous same-date totals are flagged as above. EDSM takes `Amount` straight from the procedure result (`res.Amount`).
- **PACT field verification (open):** `p4/p32AccountReceivables` return no column named `DueAmount` or `OverAmount`; the only amount is `Amount`, derived inside the procedure from the payment allocation (not the original instalment/cheque value). The procedure bodies are not in this repository, so that this is the *remaining* balance has to be confirmed against PACT before activation.
- Legacy exclusions are **off by default** (`ApplyLegacyExclusions=false`). If re-enabled, `LegacyDownPaymentFromDate` must be configured; there is no built-in date.

## Instalment status

Each instalment in an apartment's details shows its due date, the remaining unpaid amount (`RemainingAmount`, the procedure's `Amount`), two separate labels, and the original PACT `Status` text.

| Label | Source | Values |
| --- | --- | --- |
| Due timing | Due date vs. today's Dubai date only | Due Today (=), Overdue (<), Upcoming (>) |
| Payment status | Original PACT `Status`, via the verified `SourceStatusMap` only | Unpaid, Partially Paid, Paid, **Unknown** |

- A partially paid instalment that is late shows both labels (Partially Paid + Overdue).
- Payment status is never inferred from the due date, and has nothing to do with ticket or contract status.
- **Verified meaning of PACT `Status`** (from the procedure definitions as reported by the data owner): `Paid` when the remaining `Amount` is zero, otherwise `Installment`. `Installment` does **not** distinguish unpaid from partially paid, so it is never mapped to Unpaid or Partially Paid (a configured `SourceStatusMap` entry for it is ignored) and shows as **Unknown** with the original value retained. `Paid` maps to Paid only with a zero remainder; since zero-amount rows are not listed, Paid is not expected to appear. Any other value is Unknown unless explicitly mapped in `SourceStatusMap` with evidence. Distinguishing Unpaid from Partially Paid needs the original instalment amount, which the procedures do not return.
- **Amount** = `ABS(PlanFutureAmount - NewFutureAmount - PlanFutureAmount)`, i.e. the non-negative `NewFutureAmount`; threshold `Amount >= @MinAmount` (we pass 0 and keep the application's `Amount > 0` rule). `@StartDate`/`@EndDate` filter only the final `DueDate`; the ledger calculations use fixed broad dates, so choosing a month gives the **current remaining balance of instalments due in that period, not a historical balance as of that month** (the page says so for non-current periods). `@EndDate` is the last representable SQL datetime of the month's last day (23:59:59.997; .999 would round into the next day).
- **Open source-level risks (unfixed; need review of the procedure source, not yet received in this repo):** (1) `#tab` joined to `@tabpaynew` by Tag only can multiply instalments; (2) `@tabpaynew` drops `VoucherNo`/`AccountId`, so the returned voucher comes from `#tab`, not the payment-term row; (3) cumulative allocation with `DueDate <=` the current row's date double-counts same-date rows (two rows of 100, 150 paid: 50 + 50 = 100 remaining instead of 50); (4) whether `#tab`'s invoice/account joins duplicate the `PaidAmount` used for allocation is unverified. Rows are therefore preserved and same-date buckets are flagged `NeedsReview`; the application does not sum or de-duplicate by date, amount or voucher. A corrected, versioned procedure with explicit allocation and receivable identities is pending the procedure source.
- The inclusion rule is unchanged: DueAmount > 0 or OverdueAmount > 0 (monthly amounts).

## Existing EDSM exclusions (legacy, disabled by default)

With `ApplyLegacyExclusions = true`, the supplied function's filters are reproduced:

| Exclusion | Matching rule |
| --- | --- |
| Booking/down-payment rows | Exact `(UnitCode, DueDate)` returned by the supplied CRM query |
| TP121 | DueDate > 2025-12-15 |
| TP122 | DueDate > 2026-02-20 |
| TP123 | DueDate > 2026-03-20 |
| TP103, TP102, TP104, TP101 | Any unit starting with the prefix |
| Optional PDC unit list | Entire UnitCode, matching the supplied optional argument |
| Names containing `*` | Entire row |
| `Helper.SEPCIAL_CASES` | Entire UnitCode; the 50 active values supplied in `Pasted text(6).txt` are loaded in the API configuration |

The supplied `GetHandoverAndDownPaymentPaymentsAsync` method executes only the Booking & Downpayment query: join `tblPayment` to `tblLead` by `LeadID`, require payment type 2 or 3, lead status != 6, and DueDate >= `LegacyDownPaymentFromDate` (previously hardcoded 2025-11-01). The unit key is `TRIM(TRIM(l.ProjectCode) + '-' + TRIM(l.UnitNumber))`. `p.Amount` is returned but does not participate in matching. There is **no upper date limit**: the supplied `TotDate` argument is unused. The Handover/After Handover branches and project filters are commented out and remain inactive. TigerCS executes this fixed query directly with a typed date parameter, so no new CRM stored procedure is needed.

The commented TP119 condition remains inactive. String comparisons are case-sensitive; CRM keys retain the exact timestamp instead of silently reducing it to a date.

Only the active `SEPCIAL_CASES` entries were imported. Commented entries and `DN_SEPCIAL_CASES` are not added to this filter, because the original function does not use them. `GULF_CUSTOMERS` is not part of this Receivables filter either (Receivables keeps every unit's financial data); it is a Collections campaign policy, see [Collections campaign exceptions](Exceptions.md). Individual configured TP119 units are excluded, while the commented TP119 prefix/date rule remains inactive.

## Environment setup

The feature ships disabled. The CRM query and the 50 active special-case units come from the supplied EDSM source; no credentials or exclusion values are invented. Set server-side configuration/secrets:

| Setting | Required value |
| --- | --- |
| `Collections:Enabled` | `true` |
| `CollectionsSource:PactReceivables:Enabled` | `true`, after setup and validation |
| `ConnectionStrings:PACTRPT` | Connection to the report database, using an account permitted to execute the two reports and read their required dependencies |
| `ConnectionStrings:CrmDatabase` | Connection to the same CRM database used by EDSM's `connectionStrings.CRM`, with SELECT access to `tblPayment` and `tblLead`; the connection setting name is configurable |
| `CollectionsSource:PactReceivables:SpecialCaseUnitCodes` | The supplied 50 active `Helper.SEPCIAL_CASES` values are included; maintain the list if the original changes |
| `CollectionsSource:PactReceivables:SpecialCasesConfigured` | Included as `true` after importing the supplied list; a custom deployment with an empty list requires explicit confirmation that it is empty |
| `CollectionsSource:PactReceivables:PdcExcludedUnitCodes` | Optional original PDC unit exclusions; empty reproduces an omitted `pdcPayments` argument |
| `CollectionsSource:PactReceivables:Currency` | Confirmed reporting currency; default AED |

Environment-variable names use double underscores, e.g. `ConnectionStrings__PACTRPT` and `CollectionsSource__PactReceivables__Enabled`.

The supplied CRM query and `Helper.SEPCIAL_CASES` list are now incorporated. Remaining setup is the actual server-side PACT/CRM connections and build/UAT validation before activation. Missing connection/special-case configuration blocks the list and explains the missing source in the API; it does not silently omit the filter.

`ApplyLegacyExclusions = false` explicitly selects the raw PACT list and the UI identifies it. It is not the supplied filtered EDSM function and is not enabled in the shipped configuration.

`CommandTimeoutSeconds` defaults to 120 (clamped 1–300; per procedure; the two company procedures run concurrently). The API request budget is `CommandTimeoutSeconds + 30` and the Web HTTP timeout for this API is 180 s: SQL timeout < API budget < Web timeout. Each procedure logs name, rows, zero rows, elapsed time and cancellation state. `ProcedureSuffix` (empty by default) can select the reviewed `…V2` procedures in `docs/Collections/pact-sql/` once deployed. See `pact-sql/Receivables-Source-Review.md` for the source review, `MaxSourceRows` to 250000 (clamped 1–1000000). Every request reads both reports before filtering/pagination; large-source performance needs validation in the target environment. There is no cache or synchronization between independently read CRM and PACT databases.

## Validation

Automated fixtures cover no-ticket customers, the Dubai date boundary, positive sub-dirham balances, future/settled exclusion, union selection, company/tenant identity, grouping before paging, formatted mobile search, legacy cutoff boundaries, raw-row preservation, ambiguous totals, authorization before reads and source failures. Endpoint tests use the real API/JWT host with a fake PACT source, not a real SQL server. The route is included in the OpenAPI and protected-endpoint inventories.

**Real PACT/CRM and UAT checks have not been run.** Before activation, compare known customers with PACT: due today, overdue, both, partial-payment remainder, multiple units/invoices, repeated dates, booking/down-payment exclusions, special cases, and optional PDC exclusions. Confirm the complete list, procedure performance and currency. The report procedures' allocation/join issue must be checked against expected accounting figures; the UI's review state does not repair it.

Current verification: the full solution builds (Release, 0 warnings, 0 errors) and the whole test suite passes (2,870/2,870) on .NET SDK 10.0.111. The configured special-case list was compared with the supplied source: all 50 active entries match, with no duplicates and no commented/other-list entries. Real PACT/CRM validation (above) is still outstanding.
