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
