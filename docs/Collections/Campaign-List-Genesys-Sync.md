# Campaign List and Sync from Genesys

The former **Genesys Campaign History** page is now the **Campaign List** (`/Collections/CampaignList`). It is the landing page after sign-in and for `/` for anyone with Collections access, and the **Collections** sidebar item opens it. The old `/Collections/CampaignHistory` URLs still work (they are aliases), so existing bookmarks and links keep opening the same page. All stored history, the Export history tab, the detail page and every earlier filter (campaign stage, mode, employee, destination list, search) are kept; the new Company filter and the date and status filters are the primary ones.

## What the list shows

Campaign Name, Company (Dubai / Sharjah, from the campaign's units), Created Date, Total Contacts, **Sent**, **Delivered**, **Missed / Failed**, **Pending / Unknown**, Campaign Status (the existing upload status) and View Details.

Counting rules (`GenesysDeliveryRules`, one contact is counted once however many attempts it has):

| Column | Meaning |
|---|---|
| Sent | contacts with a message Genesys confirmed as sent. **Includes Delivered.** |
| Delivered | contacts with a message Genesys confirmed as delivered (`delivered`, `read`). Counted from confirmations, never `Sent - Failed`. |
| Missed / Failed | contacts where Genesys confirmed failure and no attempt was sent or delivered. |
| Pending / Unknown | uploaded contacts with no final outcome (nothing reported, `queued`, unrecognised status). Not a failure. |
| not sent (under Total) | excluded, released, not yet submitted, or upload rejected by Genesys. Kept apart from delivery failures. |

`Sent + Missed/Failed + Pending/Unknown + not sent = Total Contacts`. Message attempts are stored per message (conversation id + message id, unique), so a retry or a repeated delivery event never creates a second row and contact counts stay separate from attempt counts (shown on the detail page).

Before the first sync, delivery figures show "—" and "Not synced", never zero.

## Campaign details

Customer, Mobile Number, Message Status, Sent Time, Delivered Time, Failure Reason and Genesys Interaction ID (the conversation id), plus the existing upload facts. Several attempts show the best outcome and the attempt count.

## Sync from Genesys

A manual button (list: the campaigns on the current page, at most 25; detail: that campaign). It reads from Genesys and stores the confirmed result in two new tables, so opening the page never calls Genesys. There is **no background job**; the campaign scheduler stays disabled and the manual sending flow is untouched. A campaign whose last sync confirmed every contact is skipped by the list button; the detail button always re-reads. Interactions that are already final (all messages delivered or failed) are not read again. A failed sync keeps the last confirmed figures and says it failed. **Last synced** is shown on the list and per campaign. The send right is required (it spends Genesys API quota); viewing needs only Collections view.

Local → Genesys mapping: dispatch → its batches' `GenesysCampaignId` and `ContactListId`; contact → `CollectionsDispatchItem.GenesysContactId` (the Genesys contact id stored at upload). A Genesys interaction counts only if its `outboundContactId` is one of the dispatch's contacts, because a Genesys campaign is reused by several dispatches. A campaign uploaded without a stored Genesys campaign id cannot be matched, and the sync says so.

## Genesys APIs and permissions (read only)

| Purpose | Call | OAuth client permission |
|---|---|---|
| Interactions of a campaign | `POST /api/v2/analytics/conversations/details/query`, segment filter `outboundCampaignId` | `analytics:conversationDetail:view` |
| Message status per interaction | `GET /api/v2/conversations/messages/{conversationId}` (`messageStatus`, `messageTime`, `errorInfo`) | `conversation:message:view` |
| Existing (unchanged) | contact lists, campaigns, organization | `outbound:contactList:view`, `outbound:campaign:view` |

**Verify before relying on it:** these two calls and their field names were written from Genesys' public API shape and covered by tests against scripted responses; they have not been run against a real Genesys organization from this environment. Check on a UAT campaign that (1) the campaign is the type that produces conversations with message statuses, (2) `outboundContactId` equals the contact id Genesys returned at upload, and (3) the permission names exist in your org. A 403 names the permission to grant. Limits: the analytics query interval is read in 30-day windows (up to 12), and one sync reads at most 400 interactions per campaign (it then reports Partial; press again to continue).

## Database

Migration `AddCollectionsGenesysDeliveryStats` (script: `AddCollectionsGenesysDeliveryStats.sql`, idempotent) adds `CollectionsGenesysMessageResults` and `CollectionsGenesysSyncStates`. No existing table or column changes. Not executed by the build; run it before deploying.
