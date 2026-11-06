# Collections Campaigns: Approve & Send (one page)

Select Customers -> Approve & Send -> Genesys + Admin Email, on **Collections Campaigns**. The Receivables page stays the place for financial detail.

## Flow
1. **Filter and preview** as before (stage, preview date, project / tower, Due / Overdue, dates, minimum total, search). Due, Overdue, Total and unit columns are unchanged.
2. **Select.** Tick individual *Ready* customers (ticks survive paging), or choose *All eligible customers matching the filters* (every page).
3. **Approve & Send to Genesys.** The page re-resolves the exact selection against the stored, validated copy of the financial snapshot (the *approval data*). If that data is
   missing, failed or stale, a refresh starts and its progress (or its failure with a **Retry** button) is shown on the same page; nothing is approved from a guess.
4. **Confirm.** Shows the exact customer count, total amount, campaign stage and destination Genesys contact list. Confirming creates the approval record (who, when, exact list and
   fingerprint) and queues the job. Idempotency key + dispatch history make a second click or a second approval of the same customers a no-op.
5. **Send.** The job re-reads current balances (paid customers dropped, any other change stops the send), then uploads batches of at most 1,000 contacts. A customer is only
   *uploaded* once Genesys confirms; an unconfirmed batch is never resent and must be reconciled.
6. **Result on the same page:** Success, Partial failure or Failure, per-batch Genesys results (contact list, campaign id, batch id), and the administrator-email status.

Nothing here bypasses financial reconciliation (`Collections:Campaigns:FinancialSourceValidated`), the dispatch restrictions, or `Collections:GenesysOutbound:LiveCustomerDispatchEnabled`
(which stays off until someone enables it deliberately).

## Sending mode: Manual or Auto
The **Sending mode** selector on Collections Campaigns chooses how the schedule is treated. Neither mode dispatches anything by itself; a send always needs an authorized user to press **Approve & Send to Genesys** and confirm.

| | **Auto** (default) | **Manual** |
| --- | --- | --- |
| Date | The stage's policy schedule (Overdue: the 1st, Current Month: the 14th, Follow Up: the 28th, Legal Notice: the 12th and 14th, Legal Referral: the 30th). The page shows the calculated dates. | **Send date** picker. Only the send date being **today (Dubai)** allows a send. |
| Outside the date | *Preview only* (`OutsideSchedule`). | A later date is *Preview only / pending* (`PendingSendDate`): nothing is queued or sent, an authorized user comes back on that day. An earlier date is over (`SendDateInPast`). |
| Automatic execution | Separate switch `Collections:Campaigns:AutoSendingEnabled`, **off by default**. Nothing in the application dispatches on its own today; any future automatic executor must check it in addition to `GenesysOutbound:LiveCustomerDispatchEnabled`, the reconciliation gate and the suppression sweep. | Never automatic. |

**Manual bypasses only the stage schedule.** It does not touch: financial reconciliation and sign-off (`SourceGate`), the send permission (`CanSendReminders` / `Collections.Send`), CRM / PACT contact validation, the amount and identity checks, the shared-phone and repeated-reminder rules, the Genesys switches, the suppression sweep, the idempotency key, or duplicate-send protection (a customer already in a live or sent dispatch is left out; a unit that already has a dispatch **today** for another reminder type is held back from a manual send).

How it works: the review data keeps the honest `OutsideSchedule` reason on every record. In Manual mode a record is sendable only if that is its **only** reason, it is of the chosen stage, the data is current and was built today, and the send date is today. Each outside-schedule record also stores `ManualReminderTypeOverlap` / `ManualSharedPhone` markers (what the repeated-call rules would say if it were sent today), which hold it back in Manual mode and change nothing in Auto mode. The mode and send date are recorded in the dispatch's stored filter. The raw *Export Genesys CSV* is not available in Manual mode (use Approve & Send); the review export is.

The page shows **Ready**, **Needs review / blocked** and **Preview only** as three separate figures, a state on every row, and the exact reason when Approve & Send is unavailable.

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

## Direct Send (third sending mode)

**Sending mode** on Collections → Campaigns is **Manual**, **Auto** or **Direct Send**. Manual and Auto are unchanged.

Direct Send is for an authorized person who wants to send chosen customers to Genesys **without a campaign stage and without the Day 1 / 14 / 28 stage dates**:

* No campaign stage and no send / preview date are asked for or submitted.
* Filters: **Tower**, **Payment status** (Due / Overdue / Both), **From / To date**, **Minimum Total**, plus the customer search.
* The list shows the eligible customers with checkboxes, **Select all** on the page and **All eligible** across every page.
* **Contact list** (shown only in Direct Send): choose **Current Month** or **Follow Up**. Nothing is pre-chosen and **Send to Genesys** stays disabled until one is chosen. Both lists come from the existing `Collections:GenesysOutbound:ContactListIds` configuration; there is no separate Direct Send list.
* **Send to Genesys** opens a confirmation dialog with the selected count, the destination list (name and id), the total amount, and the campaign purpose. Nothing is sent before the confirmation, and nothing is ever dispatched automatically.

### The amount sent (calculated, never the whole balance)
For every customer the amount is the sum of the **unpaid instalments** that match the filters:

| Filter | Effect on the amount |
|---|---|
| Payment status **Due** | only instalments due inside the current Dubai month |
| Payment status **Overdue** | only instalments due before the current month |
| Payment status **Both** | both of the above (nothing from next month on is ever included) |
| **From / To date** | only instalments whose due date is inside the range (both ends inclusive) |
| **Minimum Total** | the customer is kept only when this filtered amount is **greater than** the minimum |

Example: Due AED 5,000 + Overdue AED 8,000 with Payment status **Due** sends **AED 5,000**, with the due date of that instalment. A customer whose filtered amount is zero (or not above the minimum) is not listed and cannot be sent. The same figure is shown in the customer table, the KPI total, the confirmation dialog, the audit totals, the contacts CSV and the Genesys `AmountDue`. A customer is one row; several instalments on one due date, or the same instalment under several units, are held for review instead of being summed twice. Source amounts are only read, never changed.

The unpaid instalments are stored on the customer's Direct Send review record (`CollectionsReviewRecords.InstalmentsJson`, a nullable column added by migration `AddDirectSendInstalments`; scripts `AddDirectSendInstalments.sql` and `Deploy_TigerCS_PROD_AddDirectSendInstalments.sql`). They are derived data: a review refresh rebuilds them.

### Revalidation and suppression use the same amount
Immediately before the upload the job re-reads the current balances and recalculates each customer's amount with the filter that was approved (stored with the dispatch). A customer whose selected instalments were all paid is excluded; any other change to the amount, due date, contact or validation stops the send for review. The paid-after-upload sweep compares the quoted amount with the same calculation, so a contact is made not callable only when the part that was sent changed.

### Destination checks (read-only calls to Genesys)
At approval, and again right before the upload, the chosen list is checked:

* it must exist and have the contact-template columns (`Phone`, `CustomerName`, `Email Address`, `ReminderType`, `AmountDue`, `DueDate`);
* no campaign that is **on** (or stopping) may dial it, because contacts added to it would be dialled at once. Switch that campaign off in Genesys, or choose the other list.

A check that cannot be completed refuses the send. Direct Send never creates or reuses a Genesys campaign.

### Audit
The dispatch records who approved, when, the exact list and filter (including the chosen **ContactList**), the totals, and per batch the **contact list id** and the purpose **"Direct send - manual contact list"**. The Genesys `ReminderType` column is **"Direct Send"**.

### Safety rules that still apply (Direct Send waives only the stage schedule)
| Rule | How |
|---|---|
| Authorization | Same send permission as Manual / Auto |
| Financial reconciliation, fresh data | Unreconciled or stale approval data holds every record back |
| Contact and identity validation | Same checks as the stage records |
| Paid-after-upload suppression | The same sweep, using the filtered amount |
| Duplicate-send and idempotency | One Direct Send record per customer per month; a customer already in a live or uploaded dispatch is "Already sent"; the idempotency key replays safely |
| Contact frequency | One contact per customer per business day across **all** modes (`ContactedToday`) |
| Shared phone | A number used by several customers holds all of them back |
| Already in today's campaign | A customer in a campaign scheduled today is held back (`DirectSendScheduledToday`) |
| Legal restrictions | A customer whose balance is in a legal stage is never sent directly |
| Opt-out | The repository has no opt-out / do-not-call store, so none is applied here; Genesys-side do-not-call handling on the list is outside this application |

A held-back customer is never selectable; the row shows the exact reason and the "Why customers are held back" panel gives the review step. Direct Send needs approval data built **today**; use **Refresh approval data** if the page says it is out of date.
