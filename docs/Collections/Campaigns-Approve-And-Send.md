# Collections Campaigns: Approve & Send (one page)

Select Customers -> Send to Genesys -> Genesys + Admin Email, on **Collections Campaigns** (Manual mode); Auto sends by itself on the scheduled date. The Receivables page stays the place for financial detail.

## Flow (Manual)
1. **Filter and preview**: campaign type, project / tower, Payment status (Due / Overdue / Both), From / To date, Minimum Total, search. There is no send date.
2. **Select.** Tick individual *Ready* customers (ticks survive paging), or choose *All eligible customers matching the filters* (every page).
3. **Send to Genesys.** The page re-resolves the exact selection against the stored, validated copy of the financial snapshot (the *approval data*). If that data is
   missing, failed or stale, a refresh starts and its progress (or its failure with a **Retry** button) is shown on the same page; nothing is approved from a guess.
4. **Confirm.** Shows the exact customer count, total amount, campaign stage and destination Genesys contact list. Confirming creates the approval record (who, when, exact list and
   fingerprint) and queues the job. Idempotency key + dispatch history make a second click or a second approval of the same customers a no-op.
5. **Send.** The job re-reads current balances (paid customers dropped, any other change stops the send), then uploads batches of at most 1,000 contacts. A customer is only
   *uploaded* once Genesys confirms; an unconfirmed batch is never resent and must be reconciled.
6. **Result on the same page:** Success, Partial failure or Failure, per-batch Genesys results (contact list, campaign id, batch id), and the administrator-email status.

Nothing here bypasses financial reconciliation (`Collections:Campaigns:FinancialSourceValidated`), the dispatch restrictions, or `Collections:GenesysOutbound:LiveCustomerDispatchEnabled`
(which stays off until someone enables it deliberately).

## Sending modes: exactly two, Manual and Auto
The **Sending mode** selector on Collections Campaigns has two options.

| | **Manual** | **Auto** (default) |
| --- | --- | --- |
| Who sends | An authorized user, by pressing **Send to Genesys** and confirming. Nothing is ever sent without that confirmation. | The **Auto dispatcher**, by itself, on the scheduled date, with no confirmation, and only while `Collections:Campaigns:AutoSendingEnabled` is **true** (default **false**). |
| When | **Any time.** No send date and no stage schedule. | The approved policy schedule: Overdue the 1st, Current Month the 14th, Follow Up the 28th, Legal Notice the 12th and 14th. An internal Legal referral (the 30th) is never dispatched to a customer. |
| Which campaign | **Any existing campaign type** (Overdue, Current Month, Follow Up, Legal Notice, Legal Case), subject to the legal rules below. | Each campaign scheduled today. |
| Customers | The user filters (Tower, Payment status, Date range, Minimum Total), then ticks customers or chooses **All eligible**. | Every customer who is Ready for the campaign today. |
| Amount sent | **Calculated from the filters** (see below). | The approved stage amount, unchanged. |
| Contact list | The campaign's own list, or another **existing** list that suits it (see below). | The campaign's own list. |
| This page | List, filters, selection, confirmation dialog. | A read-only preview of the schedule and the Ready figures; it sends nothing. |

### Manual
* **Contact list.** A reminder (Overdue, Current Month, Follow Up) may go to any of the three reminder lists (`CurrentMonth`, `FollowUp`, `Overdue`; for example a Current Month reminder to the Follow Up list). A Legal Notice or Legal Case goes only to its own list, so legal wording never lands in a reminder list. The campaign's own list is the default. All lists come from the existing `Collections:GenesysOutbound:ContactListIds`; there is no list of its own for Manual.
* **The amount** is the sum of the customer's unpaid instalments that match the filters:

| Filter | Effect on the amount |
|---|---|
| Payment status **Due** | only instalments due inside the current Dubai month |
| Payment status **Overdue** | only instalments due before the current month |
| Payment status **Both** (default) | both (nothing from next month on is ever included) |
| **From / To date** | only instalments whose due date is inside the range (both ends inclusive) |
| **Minimum Total** | the customer is kept only when this filtered amount is **greater than** the minimum |

  Example: Due AED 5,000 + Overdue AED 8,000 with Payment status **Due** sends **AED 5,000**, with the due date of that instalment. A customer whose filtered amount is zero (or not above the minimum) is not listed and cannot be sent. The same figure is shown in the customer table, the KPI total, the confirmation dialog, the audit totals, the contacts CSV and the Genesys `AmountDue`. A customer is one row; several instalments on one due date, or the same instalment under several units, are held for review instead of being counted twice. The campaign type decides **who is in the campaign** (the approved stage rule); the filters decide **the amount**. Source amounts are only read, never changed.
* **Confirmation dialog.** Shows the selected count, the destination list (name and id), the total amount and the campaign.
* **Audit.** The dispatch stores who approved, when, the exact list and filter (including the chosen **ContactList**), the totals, and per batch the contact list id and the campaign name.

### Refreshing the approval data (also with `BackgroundJobs:Enabled=false`)
* **Refresh approval data** uses the same `ReviewRefreshService` as always (reconciliation gate and sign-off unchanged). With Hangfire on, it runs as a Hangfire job. With `BackgroundJobs:Enabled=false`, an **in-process worker** runs it instead (`InProcessReviewJobScheduler`): one refresh at a time (a database-level single active run plus an in-process gate), cancelled on application shutdown, and a refresh that was just published for the same business day is returned instead of repeated for `Collections:Review:InProcessRefreshCooldownSeconds` (default 60; 0 turns it off).
* The in-process worker runs **only the refresh**. No recurring job is registered, nothing is sent automatically, and **sending still needs real background jobs**: dispatch and the paid-after-upload sweep are not run in process.
* The page shows the status of the run and any error (for example a financial snapshot that is not loaded, or an unreconciled source), and a notice when the data is already current.

### Export review CSV (Manual and Auto)
* **Export review CSV** is available in both modes (permission `Collections.Send`). In Auto it is the established review file of the schedule preview. In Manual it downloads **every customer of the filtered list**, held-back ones included, with the filtered amount shown on the page, the status and the exact reason. It is for people to review, not for Genesys; it uploads nothing, is audited (`CollectionsReviewFileExported`: counts, filter, hash, no customer data) and refuses lists above 20,000 rows (narrow the filters). The whole list is calculated once, so the file is complete and consistent with the page however many customers it holds.

### Amounts, date range and units
* **Date range.** Manual amounts come from the approval data, which holds **every** unpaid instalment of a customer from the start of the history (2000) on, exactly the period the Auto preview reads by default. The stage policy (who is a candidate, the stage amount) still looks only from the configured PACT start date (`CollectionsSource:PactReceivables:StartDate`) on, as in Auto. A **From** date earlier than the approval data's start is **refused** in the preview, the export and the review file, with the start named: an amount that quietly left out older instalments would be understated. No From date means the whole history. To, Payment status and Minimum Total are applied as described above.

### Manual and Auto show the same customers (the "0 customers" defect)
With identical filters the Auto preview showed 37 customers (34 Preview Only, 3 Blocked) and Manual showed none ("No customers match these filters") although the approval data was current. Four differences were found and removed:
* **Payment-status default (the cause of the zero).** The Manual page sends no review-list payment status (its Payment status is Due / Overdue / Both), and the API defaulted the blank value to *Unpaid or partially paid*, which is applied to the **stored** status. At the time every open balance had the status **Unknown** (the report cannot tell unpaid from partially paid), so every record was dropped before the list was built. Since the remaining-amount rule (see *Payment classification* in Collections-Review-And-Genesys-Dispatch.md) a known positive remainder is **Unpaid** and eligible; only a missing, negative or contradicted remainder is Unknown. Auto never applies that filter. Now the review-status filter applies only when it is chosen explicitly; an Unknown record is **listed and held back with its reason** (`PaymentStatusUnknown`, "stays visible but cannot be sent"), never hidden. Note: that rule holds the customer back in both modes' send pipeline (the Auto dispatcher uses the same records); the Auto *preview* does not show it. Lifting it is a business decision, not a code fix.
* **Per-instalment minimum.** The refresh and the revalidation read the source with a per-instalment minimum of 100 while Auto read with 0 and applied Minimum Total to the customer's total. A customer with instalments of 60 + 70 was in Auto (130) and absent from Manual; 150 + 60 was 210 in Auto and 150 in Manual. All review reads now use no per-instalment minimum.
* **Read window.** The approval data started at the PACT start date while Auto's Due / Overdue / Total and Minimum Total used the whole history, so a customer with older arrears had a smaller Overdue in Manual or dropped out under Minimum Total. The approval data now holds the whole history; the stage policy keeps its start date.
* **Tower and search.** The snapshot procedure returns the tower number, not the tower id the Manual list filters by, so a Manual tower filter matched nobody; the source now resolves the id from `dbo.CollectionsTowers`, and the Manual page says when tower data is missing. The Manual search now matches the same fields as Auto (name, customer id, unit code, e-mail, tower, phone digits).
A Manual list is chosen by From and To dates (a month is refused), and a list larger than one pass can read is refused, never truncated.
* **Several units (sending).** Each unit is its own record with its own amount and earliest qualifying due date; units are never merged for a send. A phone number that is used on more than one unit (or several customers) is **held back from sending in full** (`ManualSharedPhone` / `SharedPhoneMultipleUnits`), so a send never dials one number once per record. Different numbers on different units give one row per unit, each with its own amount and due date. The file export handles the same case deliberately (below).

### Export for Genesys (Manual and Auto): a download, not a send
* A separate **Export for Genesys** button (permission `Collections.Send`) downloads a CSV for a manual import into a Genesys contact list. It is available in **both modes** and is **independent of sending eligibility**: it needs the permission, approval data (a refresh has been run) and customers matching the filters, and nothing else. Reconciliation (Ready / Failed), `PaymentStatusUnknown`, Ready / Needs review / Held back / Already sent, the stage schedule, the Genesys OAuth configuration, `BackgroundJobs` and the live-dispatch switch never block it; what is unverified is **labelled** on the file instead. No customer has to be ticked and no approval or confirmation is involved: with nothing ticked (and always in Auto) the file holds **every customer matching Tower, Payment status, From / To and Minimum Total**, held-back ones included; ticks in Manual only narrow it. The old Auto "Export Genesys CSV" link (a different, unchecked and unaudited file) is gone.
* **Who and how much.** Both modes build the same population with the same amounts: the one Manual calculation over each unit's stored instalments (`ReviewQueryService.ResolveForExportAsync`, a path of its own that never touches the send resolution). Due = instalments classified Due in the range, Overdue = those classified Overdue, Both = Due + Overdue; Minimum Total applies to the customer's total; a customer with nothing qualifying under the filters is not in the file. The amount and the earliest qualifying due date are exactly what Preview shows. A From date before the approval data's start, or a month filter, is refused, as in the preview: an amount that quietly left out instalments would be wrong, not unverified. A unit whose amount cannot be calculated (ambiguous or unreadable instalments) is **left out and counted** (`AmountUnavailable`), and so is a unit whose source amount has unresolved decimals (`AmountPrecisionUnresolved`: the stored value is the raw one, and a two-decimal `AmountDue` would be a rounded guess): no financial value is ever invented.
* **Rows** (`GenesysExportRows`, deterministic). **One row per customer unit, exactly the records Preview lists**, each built by `DispatchService.ToPayload()` and written by `GenesysContactTemplate.WriteCsv()`, so every value is exactly what a real upload would send and each row keeps its own AmountDue, DueDate and ReminderType. A phone number is a value in the row, **never a key**: records are never merged, added up, de-duplicated or left out because of it (the same customer on several units = one row per unit; different customers on one number = one row each; the same customer and number on several units = one row per unit). A unit is left out only when a mandatory field is genuinely missing, and it is then **named** with its reason in the notice, the page and the API (`ExcludedRecords`): `AmountUnavailable` (no calculable amount or due date), `AmountPrecisionUnresolved` (unresolved decimals, never rounded), `NoCallablePhone` (no E.164 number; nothing substituted). A unit's status (Needs review, held back, already sent) and the reconciliation state never keep it out. Send now follows the same per-unit rule: a number shared by several units never holds a send back either (each unit is its own call).
* **Columns** are the contact-template columns, in order: `Phone` (E.164), `CustomerName`, `Email Address`, `ReminderType` (`Current Month` / `Follow Up` / ...), `AmountDue` (`0.00`), `DueDate` (`yyyy-MM-dd`); every value is text. Further columns the list itself has are added empty. Spreadsheet formula characters are neutralised; quotes, commas and line breaks are quoted per RFC 4180. The destination is the campaign's own list, or in Manual the list chosen in the **Contact list** dropdown (the same rule as a send).
* **Schema.** When the Genesys integration can read the chosen list, its real columns are used, and a list that lacks or misnames a template column **refuses** the export with the exact incompatibility (never guessed around). When the list **cannot be read** (integration not configured, login refused such as `HTTP 400, invalid_client`, Genesys unreachable or answering with an error), the documented six-column template is used and the file is labelled **"Template not verified against Genesys"** with the reason: in the file name (`genesys-CurrentMonth-to-FollowUp-20261106-0930 (Template not verified against Genesys).csv`), in the notice shown on the page's next view (the download itself does not reload the page) and in the audit record (`SchemaVerified=False`). The CSV itself stays the plain six-column template so it can be imported as it is. A real **Send to Genesys** still needs a valid OAuth login and a readable, fitting destination list.
* **Unverified data.** An unreconciled source, stale approval data (the file states the actual age and read time), data built for another day, or included units whose data needs review (`PaymentStatusUnknown` among them: the source amounts are written as they are, never reclassified as unpaid) never stop the file. They label it **"Financial data not verified"** in the file name, the notice and the audit record (`DataVerified=False;Unverified=StaleSource,PaymentStatusUnknown:12,...`). Units held back from sending by a policy rule (legal release, shared phone, already sent, contacted today) are counted separately (`IncludedByStatus=Ready:30,NeedsReview:5,Excluded:2`) and the notice says how many such units the file holds.
* **Not a send.** The export is download only: it uploads nothing, creates no dispatch, triggers no call and does **not** mark customers as sent, because a file is no proof that Genesys received anything. Every send rule (Ready status, reconciliation, freshness, built today, shared phone, legal, already sent, contacted today, idempotency, OAuth and destination checks) is unchanged and applies to real sends only; a customer who was exported can still be sent, once, and a customer who cannot be sent still cannot.
* **Audit and repeats.** Every export is audited before the file is released (`CollectionsGenesysFileExported`: who, when, campaign, list, rows, units, combined units, exclusions by reason, total, statuses, data and schema verification, mode, the filter (without the customer search text), selection, SHA-256, and a one-way fingerprint of each customer's unit and number; no names, phones or e-mails). If the audit entry cannot be written, **no file is released**. The fingerprints of the last `Collections:Review:ExportRepeatLookbackDays` days (default 30; 0 turns it off) say how many of the file's customers were exported before (by unit **and** by number, so another campaign type, month or unit still counts): the notice and the audit record say so (`Repeated=n;RepeatCheck=Checked`), and the download is **never held and needs no confirmation**. If the earlier exports cannot be read (`RepeatCheck=Unavailable`) or the window holds 5,000 exports or more (`RepeatCheck=Incomplete`) the file is still made and says the count is missing or incomplete. The repeat count and the audit entry run under an **export lock** (an in-process gate, and a SQL Server application lock when the database is SQL Server) so two simultaneous exports see each other's entries; one that cannot get in within 20 seconds is refused with "Try again" and writes nothing. The **Export for Genesys** button is held for a few seconds after a press, so a double click sends one request. A refused export returns to the same mode, filters and contact list with the exact reason. The file has a UTF-8 BOM. A list of more than `Collections:Review:MaxSelection` customers is refused with "narrow the filters", never cut short.

### Auto
* **The Auto dispatcher** (`AutoDispatchService`, Hangfire job `collections-auto-dispatch`, cron `Collections:Campaigns:AutoDispatchCron`, default hourly 08:00-16:00 Dubai) is **registered only while `AutoSendingEnabled` is true and the Genesys upload is enabled**. With the shipped defaults it does not exist and does nothing.
* On a scheduled date it makes sure the review data was built **today** (it refreshes it itself otherwise), resolves the Ready customers of each scheduled campaign, and creates the dispatch through the same pipeline as a person's confirmation. One dispatch per campaign per business day (idempotency key `auto:<date>:<campaign>`), so the hourly job can never send twice.
* The audit record names the system as the initiator ("Automatic (scheduled)"). Turning the switch on is the operator's acknowledgement that the campaign's own Genesys campaign dials what is uploaded to its list.
* A Legal Notice is dispatched only once legal notices are released (`LegalNoticeExportEnabled`); a Legal referral never.

### Rules that apply in both modes
Manual waives **only the stage schedule**; Auto follows it. Everything else is the same:

| Rule | How |
|---|---|
| Authorization | Manual needs the send permission; Auto runs as the system only while the switch is on |
| Financial reconciliation, fresh data | Unreconciled or stale review data holds every record back |
| Customer and contact validation | Valid international phone, CRM/PACT conflicts, unit and customer identity, amount precision |
| Shared phone | A number used by several customers holds all of them back |
| Legal restrictions | Legal notices only when released; an internal Legal referral is never sent to a customer |
| Repeated calls | Per **unit**: a unit that is also in another campaign scheduled today is held back from a Manual send of a different campaign. A phone number shared by several units (one customer or several) never holds a unit back: each unit is its own contact and its own call |
| Contact frequency | One contact per **unit** per business day across **both** modes (`ContactedToday`, keyed by company, tenant and unit code); another unit on the same number is not blocked by it |
| Duplicate-send and idempotency | One record per **unit**, campaign and month (the record key: company, tenant, unit id, unit code, cycle); a unit already in a live or uploaded dispatch is "Already sent" (filtered unique index over live items); the idempotency key replays safely. Never keyed by phone number |
| Revalidation | Right before the upload the job re-reads current balances, recalculates each customer's amount with the filter that was approved (Manual) or the stage amount (Auto), drops customers who paid, and stops the send for review on any other change |
| Suppression | The paid-after-upload sweep compares with the same amount, so a contact is made not callable only when the part that was sent changed |
| Destination checks | At approval and again right before the upload: the list must exist with the contact-template columns (`Phone`, `CustomerName`, `Email Address`, `ReminderType`, `AmountDue`, `DueDate`) and **no other active campaign** may dial it (the campaign's own stage campaign dialling its own list is the intended path). A list that cannot be verified is never used |
| Opt-out | The repository has no opt-out / do-not-call store, so none is applied; Genesys-side do-not-call handling on the list is outside this application |

A held-back customer is never selectable; the row shows the exact reason and the "Why customers are held back" panel gives the review step. Manual needs review data built **today**; use **Refresh approval data** if the page says it is out of date.

### Review data and the one schema change
Each stage record stores the customer's unpaid instalments (`CollectionsReviewRecords.InstalmentsJson`, a nullable column added by migration `AddDirectSendInstalments`; scripts `AddDirectSendInstalments.sql` and `Deploy_TigerCS_PROD_AddDirectSendInstalments.sql`). The filtered amount cannot be calculated without them (the review pages never call the financial source), so the column is required. The name is historical (the migration was created while this was developed as a separate mode); it is kept unchanged so that an environment that already applied it stays compatible. The column is derived data: a review refresh rebuilds it, and a record written before it existed makes a Manual send ask for a refresh.

## Retrying
* **Genesys:** rejected or released customers are free again; preview the list and press Approve & Send (customers Genesys already accepted show as already sent and are left out).
  Unconfirmed batches: reconcile first.
* **Refresh of the approval data:** the Retry button on the Campaigns page.
* **Administrator email:** *Retry administrator email* resends the report only. The Genesys upload is never repeated.

## Administrator email
After a send finishes (Completed or Completed with errors) the administrator(s) receive the approved customer list and the actual Genesys results: campaign name, approval time, approving
employee, customer count, amounts, uploaded / rejected / unconfirmed / excluded counts, and the Genesys batch references (contact list, campaign id, batch id). The attachment is a CSV;
phone numbers and emails are masked, formula-looking text is defused, the body carries totals only, and nothing sensitive is logged.

```json
"Collections": { "Campaigns": { "AdminNotification": {
  "Enabled": true,
  "Recipients": [ "admin1@example.com", "admin2@example.com" ],
  "AttachCustomerList": true,
  "AttachmentFormat": "Csv"
} } }
```
* Recipients live only in configuration: `appsettings.json`, `appsettings.UAT.json` (committed with the UAT recipient), or environment variables
  (`Collections__Campaigns__AdminNotification__Recipients__0=...`; one entry may also hold several addresses separated by `;` or `,`). Restart the API to pick up a change; no rebuild.
* Enabled with no valid recipient, an unsupported `AttachmentFormat`, or a mail provider that only records (`EmailNotifications:Provider = Recording`) is shown as **Not configured / Failed**, never as sent.
* The result is stored on the dispatch (`AdminEmailStatus`: None, Sending, Sent, Failed, NotConfigured, Disabled) apart from the Genesys status. A mail failure never changes or resends the Genesys batch.
* The UAT customer-delivery protections (`VerificationTesting`: email / SMS override, block real customers) apply to customer messages and are untouched; the administrator report is an internal
  message sent through the configured `EmailNotifications` provider (`Smtp` requires `EmailNotifications:Enabled`, host, `FromEmail`, and the password via secret/environment).

## Database
Migration `20261021120000_AddAdminEmailToCollectionsDispatch` (script `AddAdminEmailToCollectionsDispatch.sql` in the repository root) adds five columns to `CollectionsDispatches`.
