# Tiger CS documentation set

Maintained set describing the **Tiger customer‑service system** (this repository, `TigerCS`) and its integrations (Genesys Cloud, TigerGroupWeb, Tiger CRM, PACT, EDSM). Baseline: `main` @ `a1cba71` plus the review branch `claude/compassionate-tesla-albggj`. Accuracy rule: *current behaviour is read from code*; drafts, proposals and external assumptions are labelled as such.

| # | Section | File |
|---|---|---|
| 1 | System overview, architecture, dependency map, branch/PR baseline | [01-system-overview.md](01-system-overview.md) |
| 2 | User guide per module and role | [02-user-guide.md](02-user-guide.md) |
| 3 | Role / permission matrix | [03-roles-and-permissions.md](03-roles-and-permissions.md) |
| 4 | Ticket lifecycle, assignment, SLA, approvals, notifications | [04-ticket-lifecycle-sla-approvals-notifications.md](04-ticket-lifecycle-sla-approvals-notifications.md) |
| 5 | Customer identity and CRM/PACT reconciliation | [05-customer-identity-and-reconciliation.md](05-customer-identity-and-reconciliation.md) |
| 6 | Database, migrations, SQL scripts | [06-database-and-migrations.md](06-database-and-migrations.md) |
| 7 | REST API reference | [07-rest-api-reference.md](07-rest-api-reference.md) |
| 8 | Genesys integration guide and workflows | [08-genesys-integration-guide.md](08-genesys-integration-guide.md) |
| 9 | Data Action inventory and import | [09-data-action-inventory.md](09-data-action-inventory.md) · JSON in [`../Genesys/data-actions`](../Genesys/data-actions) |
| 10 | Collections sources, financial semantics, campaigns, reminders | [10-collections-and-campaigns.md](10-collections-and-campaigns.md) |
| 11 | Verification (OTP) and document delivery | [11-verification-and-document-delivery.md](11-verification-and-document-delivery.md) |
| 12 | Configuration reference | [12-configuration-reference.md](12-configuration-reference.md) |
| 13 | Deployment order | [13-deployment-order.md](13-deployment-order.md) |
| 14 | Troubleshooting | [14-troubleshooting.md](14-troubleshooting.md) |
| 15 | **Requirements traceability matrix** and outstanding decisions | [15-requirements-traceability-matrix.md](15-requirements-traceability-matrix.md) |
| 16 | UAT checklist and validation evidence | [16-uat-checklist.md](16-uat-checklist.md) |
| 17 | Release package, stale artifacts, credential rotation, migration/config checklist | [17-release-package-and-credentials.md](17-release-package-and-credentials.md) |
| 18 | Remaining work by blocker type, owner, next action | [18-remaining-blockers.md](18-remaining-blockers.md) |
| 20 | SMTP credential exposure: what leaked, rotation steps, guards | [20-smtp-credential-exposure-and-rotation.md](20-smtp-credential-exposure-and-rotation.md) |

Audit findings backing the matrix: [identity & lifecycle](audit-findings-identity-lifecycle.md) · [Genesys & customers](audit-findings-genesys-customers.md) · [collections & services](audit-findings-collections-services.md).

## Older documents
The pre‑existing documents under `docs/` (architecture, design, Genesys, Collections, releases) remain as detailed references, but **where they disagree with this set, this set (and the code) wins**. Known stale: `docs/releases/UAT-Outstanding-Requirements-Readiness.md` (marked superseded); `docs/architecture/*` and `docs/design/*` describe the MVP design and may predate implementation; root‑level `*.patch` files and `publish/` are build leftovers, not documentation ([15 D15](15-requirements-traceability-matrix.md#154-outstanding-decisions)).
