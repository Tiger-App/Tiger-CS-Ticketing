# Collections campaign exceptions

Two independent, unit-level exclusion policies for Collections campaigns, and a manual inclusion for one of them. Configuration-driven, audited, and enforced at every step. Nothing here enables automatic sending
(`Collections:Campaigns:AutoSendingEnabled` stays `false`) and nothing changes a unit's receivable (Receivables, the snapshot and the stored financial figures are untouched).

## Policies

| Policy | Configuration (`CollectionsSource:PactReceivables`) | Rule |
| --- | --- | --- |
| Permanent special cases | `SpecialCasesConfigured`, `SpecialCaseUnitCodes` | Excluded from every campaign on every date, in Manual and Auto. Nothing overrides it. |
| GULF_CUSTOMERS / CHEQUES | `GulfCustomersConfigured`, `GulfCustomerUnitCodes`, `GulfCustomersInclusionDay` (14) | Excluded, except in a **Manual** campaign on the 14th of the month (Dubai date) or when an authorized person includes the unit manually in that campaign. Auto never includes them. |

* A unit in both lists stays permanently excluded (the special case wins, even on the 14th and even if someone included it).
* Matching is exact after normalization (trimmed, upper case). No prefix, wildcard or partial match. Only the listed unit is affected: other units of the same customer, and units that share a phone number, are untouched and are never merged.
* The day is the Dubai calendar day (`Collections:TimeZoneId`, `Asia/Dubai`), so 00:00-23:59 Dubai is "the 14th" whatever the UTC date.
* The 14th is an **eligibility window, not a schedule**: GULF_CUSTOMERS units become eligible for Manual Preview, Export, Approval and Send that day, subject to every existing rule. They are not sent automatically.

### Loading the GULF_CUSTOMERS list

The list is configuration, not code. It ships **empty and switched off** (`GulfCustomersConfigured=false`); load the business-supplied list and switch it on in the deployment's settings:

```
CollectionsSource__PactReceivables__GulfCustomersConfigured=true
CollectionsSource__PactReceivables__GulfCustomerUnitCodes__0=TP...-...
CollectionsSource__PactReceivables__GulfCustomerUnitCodes__1=...
```

(or the same keys in `appsettings.{Environment}.json`). `SpecialCasesConfigured`/`SpecialCaseUnitCodes` are the existing keys; with `SpecialCasesConfigured=true` the 50 configured special cases are now also excluded from the campaigns
(before, they only applied under `ApplyLegacyExclusions`, which is off).

## Where it is enforced

| Place | How |
| --- | --- |
| Stored review data (refresh and the revalidation read) | `ReviewRecordBuilder` stores `SpecialCaseExcluded` / `GulfCustomerExcluded` on every record of a listed unit (status Excluded). Figures are unchanged. |
| Manual Preview, Export for Genesys, Approval | `ReviewQueryService` decides again at read time with today's Dubai date and the campaign's manual inclusions; excluded units are not listed, exported or approvable. A GULF_CUSTOMERS unit that is allowed carries a warning (`GulfInclusionDay` or `ManuallyIncluded`). |
| Auto Preview and its Genesys file | `CollectionsCampaignAppService` drops the units' rows from the campaign (the receivables snapshot is not touched); the data-store engine is not used while an exclusion is configured. |
| Auto dispatcher | Resolves through the same query: special and GULF_CUSTOMERS units are never Ready for Auto, not even on the 14th. |
| Send to Genesys | `DispatchService` re-evaluates every approved item **immediately before the upload** with the current date and the current inclusions: an approval made on the 14th does not upload after midnight, a removed inclusion is not uploaded, a special case added after approval is not uploaded. Only that unit is dropped (with a reason); the rest of the dispatch continues under every other check. |

All financial, authorization, legal, contact, reconciliation, idempotency and duplicate-dispatch checks are unchanged.

## Manage Exceptions (manual inclusion)

`/Collections/Exceptions` (compact **Manage Exceptions** button on Collections Campaigns, which follows the chosen campaign type). Needs the send right (`CollectionsSend`), like Manual Export and Send.

* Lists the GULF_CUSTOMERS units of the current approval data for the campaign type, searchable by unit code, tower and customer, with their state (**Manually Included**, not included, permanently excluded special case, other checks holding the unit).
* Tick specific units and **Add selected to Manual campaign**; **Remove inclusion** withdraws them before sending. Nothing is included by default; selecting some units never includes the rest.
* An inclusion belongs to one Manual campaign: `Manual:{Dubai date}:{campaign type}`. It is not permanent (tomorrow is a new campaign) and applies to Manual only.
* The API re-checks every unit against the stored approval data and refuses special cases, non-GULF units and unknown keys; the client's unit key is a look-up, never a fact. Preview, Export, Approval and the upload read the inclusion again from the database.
* Each change is audited (`CollectionsGulfInclusionAdded` / `CollectionsGulfInclusionRemoved`: actor, time, campaign key, unit code; no customer name, phone or e-mail) and kept in `CollectionsExceptionInclusions`
  (`IncludedByEmployeeId`, `IncludedAtUtc`, `CampaignKey`, and `RemovedByEmployeeId`/`RemovedAtUtc` on removal). One active row per unit and campaign is enforced by a filtered unique index.

API: `GET api/collections/review/exceptions`, `POST .../exceptions/include`, `POST .../exceptions/remove`.

## Logging

One line per excluded unit: stage, unit code and reason code (`SpecialCaseExcluded`, `GulfCustomerExcluded`). No customer name, phone, e-mail or amount. Preview listings log at Debug; Approval, Export, Auto and the upload log at Information.

## Database

Migration `20261109120816_AddCollectionsExceptionInclusions` adds one table (`AddCollectionsExceptionInclusions.sql`, idempotent; not executed by the change).
