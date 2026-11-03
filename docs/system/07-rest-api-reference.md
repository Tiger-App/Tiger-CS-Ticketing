# 07 - REST API reference (TigerCS.Api)

Status: generated from the code at the working tree after commit `31878f4` (base `a1cba71` plus the CRM document gateway and email-OTP commits). The code is the source of truth; every row cites the controller file. External systems (TigerGroupWeb, Genesys, CRM, PACT, EDSM) were not accessible, so anything said about them is an **unverified external assumption**.

## 0. Conventions that apply to every route

| Topic | Behaviour | Source |
|---|---|---|
| Base | `https://<TigerCS.Api host>/` (Web calls it server-side with the user's bearer token). Genesys never calls TigerCS directly: Genesys -> TigerGroupWeb -> TigerCS. Public Genesys-facing URL = `https://tigergroup.ae` + the same path (unverified, per `docs/Genesys/TigerGroupWeb-Proxy-Change.md`, which says TigerGroupWeb forwarding is PENDING and not in this repo). | `docs/Genesys/TigerGroupWeb-Proxy-Change.md` |
| Auth scheme | JWT Bearer (HS256, issuer/audience/signing key from `Jwt:*`). Token from `POST /api/auth/login`. | `Program.cs:48-72` |
| Fallback policy | Every endpoint requires an authenticated, **active** employee unless `[AllowAnonymous]`. Controllers with no `[Authorize]` (`DepartmentsController`, `ChannelsController`, `AuthController.Logout`) therefore still require a token. | `InfrastructureServiceCollectionExtensions.cs:393-405` |
| System Administrator override | Every policy (not role checks inside app services that are not routed through `AuthorizationGate`) also admits `System Administrator` (ADR-0024). | `SystemAdministratorOverrideHandler`, `PolicyNames.cs` |
| Anonymous routes | `GET /health`, `POST /api/auth/login`, `POST /api/auth/screen-pop/redeem`. | `Program.cs`, `AuthController.cs` |
| Error body | RFC 7807 `ProblemDetails` (`application/problem+json`), `type` usually `https://tigercs.internal/problems/<slug>`; validation failures use `ValidationProblemDetails` (`errors` map). Unhandled exceptions: `AddProblemDetails()` 500. A request aborted by the client is logged and answered 499. | `Program.cs`, `UseTigerCsClientDisconnectHandling` |
| Empty 403 | `Forbid()` is used in several ticket/note/verification actions; with JWT bearer it yields a **403 with an empty body** (no ProblemDetails). Collections and document routes return ProblemDetails with a `code`. | `TicketsController.cs`, `VerificationSessionsController.cs` |
| 401 | Missing/invalid token -> 401 from the auth middleware (no body). | framework |
| Casing | System.Text.Json web defaults: camelCase properties. Enums are numbers unless the DTO carries `[JsonStringEnumConverter]` (only `CrmDocumentCopyStatus`, `CustomerOtpStatus`); most request DTOs take enum names as **strings** (e.g. `"InProgress"`). Admin DTOs use real enum types (`AssignmentMode`, `ApprovalTargetKind`, `SlaTriggerType`, ...) - see Swagger. | grep `JsonStringEnumConverter` |
| Concurrency | Ticket mutations carry `rowVersion` (base64 `byte[]`); stale -> 409. | ticket DTOs |
| OpenAPI | `/swagger` and `/swagger/v1/swagger.json` exist only in `Development` and `Testing`. | `OpenApiDocumentation.cs:40` |
| Idempotency | `Idempotency-Key` header: verification sessions (optional), collections `POST reminders` and `.../outcomes` (optional/recommended), `POST /api/genesys/documents/send-copy` (**required**, 1-128 chars `[A-Za-z0-9._:-]`). | controllers |
| Deadline header | Collections payment reads accept `X-Collections-Deadline-Seconds` (whole seconds; can shorten, never lengthen, the configured budget). | `GenesysCollectionsController.cs:416-423` |

### Policy catalog (what the policy names mean)

| Policy | Roles admitted (+ System Administrator via override) |
|---|---|
| `AuthenticatedStaff` | any active authenticated employee |
| `CustomerVerification` | CS Agent, CS Supervisor |
| `SupervisorOrAbove` | CS Supervisor, CS Manager, General Manager, Chairman/CEO |
| `DepartmentHeadOrAbove` | Department Head, CS Manager, General Manager, Chairman/CEO |
| `CsManagerOrGeneralManager` | CS Manager, General Manager, Chairman/CEO |
| `SystemAdministrator` | System Administrator |
| `DepartmentScoped` | resource-based, not used as an attribute on any current controller |

Roles: CS Agent, CS Supervisor, Department Employee, Department Head, CS Manager, General Manager, Chairman/CEO, System Administrator, Reporting User (`Roles.cs`).

Common error rows are not repeated per route: **401** (no/invalid token), **403** (policy), **500** (unexpected).

---

## 1. Platform

### HealthEndpoint (minimal API, `Program.cs`)
| Method | Route | Auth | Success | Notes |
|---|---|---|---|---|
| GET | `/health` | anonymous | 200 `{"status":"healthy"}` | liveness only |

### AuthController - `api/auth` (`AuthController.cs`)
| Method | Route | Auth | Request body | Success | Errors |
|---|---|---|---|---|---|
| POST | `/api/auth/login` | anonymous | `{username*, password*}` | 200 `LoginResponseDto` (accessToken, expiry, employee, roles, primary department) | 400 blank field (ValidationProblem); 401 `invalid-credentials`; 423 `account-locked` |
| POST | `/api/auth/screen-pop/redeem` | anonymous (one-time token is the credential) | `{token*}` | 200 `ScreenPopSessionResponseDto` (session + `targetPath`) | 400 token blank; 401 `SCREEN_POP_TOKEN_INVALID` / `_EXPIRED` (>1 h) / `_USED`; 403 `GENESYS_AGENT_NOT_MAPPED` / `GENESYS_AGENT_INACTIVE`; 503 `genesys-integration-disabled` |
| POST | `/api/auth/change-password` | AuthenticatedStaff | `{currentPassword*, newPassword*}` | 204 (all sessions invalidated) | 400 blank; 422 `current-password-incorrect` or `password-policy-violation` (`errors.NewPassword`) |
| POST | `/api/auth/logout` | any authenticated | none | 204 always | - |

Sample: `curl -X POST $API/api/auth/login -H 'Content-Type: application/json' -d '{"username":"u","password":"p"}'`.

### UsersController - `api/users` (`UsersController.cs`)
| Method | Route | Auth | Body / query | Success | Errors |
|---|---|---|---|---|---|
| GET | `/api/users/assignable` | CsManagerOrGeneralManager | - | 200 list of `AssignableUserDto` | 403 |
| GET | `/api/users/me` | AuthenticatedStaff (fallback) | - | 200 `CurrentUserResponseDto` | 401 if token subject unresolved |
| PATCH | `/api/users/{employeeId:guid}/activation` | SystemAdministrator | `{isActive*, reason?}` | 200 `ActivationResponseDto` | 400; 404; 409 `last-admin` |

### RolesController - `api/roles`
| GET | `/api/roles` | SystemAdministrator | - | 200 list of `RoleDto` (name, description) | 403 |

### DepartmentsController - `api/departments` (no attribute; fallback = authenticated)
| Method | Route | Query | Success | Errors |
|---|---|---|---|---|
| GET | `/api/departments` | `activeOnly=true` | 200 `DepartmentDto[]` | - |
| GET | `/api/departments/{departmentId:int}/users` | `activeOnly=true, page=1, pageSize=25` (clamped 1-100) | 200 `PagedResultDto<DepartmentUserDto>` | 404 |

### ChannelsController / CategoriesController / RequestTypesController (reference data)
| Method | Route | Auth | Query | Success |
|---|---|---|---|---|
| GET | `/api/channels` | authenticated (fallback) | `activeOnly=true` | 200 `ChannelDto[]` |
| GET | `/api/categories` | AuthenticatedStaff | `departmentId?` | 200 `CategoryDto[]` (empty list for unknown department) |
| GET | `/api/request-types` | AuthenticatedStaff | `departmentId?` | 200 `RequestTypeOptionDto[]` (active only) |

### DashboardController - `api/dashboard` (AuthenticatedStaff; scope = caller's visible departments)
| GET | `/api/dashboard` | - | 200 `DashboardSummaryDto` |
| GET | `/api/dashboard/overview` | query `DashboardOverviewRequestDto`: `dateFrom, dateTo (DateOnly), departmentId, ownerEmployeeId, channelId, requestTypeId, ticketStatus, priorityId` (all optional; default last 30 UTC days) | 200 `DashboardOverviewDto` |

### ReportsController - `api/reports` (CsManagerOrGeneralManager)
| Method | Route | Query | Success | Errors |
|---|---|---|---|---|
| GET | `/api/reports/team-performance` | `dateFrom, dateTo, employeeId, agentType ("CS Agent"/"Call Center Agent"), departmentId` | 200 `TeamPerformanceReportDto` | 403 |
| GET | `/api/reports/team-performance/records` | `employeeId*, metric* (CurrentlyAssigned/TicketsWorked/CompletedFollowUps/SlaBreaches), dateFrom, dateTo` | 200 `TeamPerformanceRecordsDto` | 400 `Unknown metric`; 404 `Employee not on the report` |

---

## 2. Intake, verification, lookup, tickets (agent workflow)

All routes below are `CustomerVerification` (CS Agent / CS Supervisor / SysAdmin) unless stated.

### IntakeRecordsController - `api/intake-records`
| POST | `/api/intake-records` | body `CreateIntakeRecordRequestDto`: `channelId*` (id or code string), `phoneNumber*`, `departmentId?`, `isUnitRelated*`, `rawUnitNumberEntered?`, `priorityHint?` (1-4) | 201 `IntakeRecordResponseDto` + Location | 400 channel not found / inactive / phone required (ValidationProblem on `channelId`/`phoneNumber`); 404 `department-not-found` |

### CustomerLookupController - `api/intake-records/{intakeRecordId:long}/customer-lookup`
| GET | `.../customer-lookup` | - | 200 `CustomerLookupResultDto` (one entry per searched source: CRM/PACT/Tasleeh, each Found/NotFound/Failed) | 404 |

### CrmController - `api/crm`
| Method | Route | Params | Success | Errors |
|---|---|---|---|---|
| GET | `/api/crm/units/{crmUnitId}` | - | 200 `UnitVerificationResponseDto` | 404 `unit-not-found`; 502 `crm-unavailable` |
| GET | `/api/crm/units/search` | `unitNumber*`, `propertyName?` | 200 list | 400; 502 |
| GET | `/api/crm/units/{crmUnitId}/contacts` | - | 200 `ContactVerificationResponseDto[]` (Owner/Tenant/Representative) | 404; 502 |
| GET | `/api/crm/buyers` | `phoneNumber*` | 200 `CrmBuyerMatchDto[]` (at most 1 customer) | 400 (blank, or `crm-invalid-response`); 401 `crm-unauthorized`; 404 `buyer-not-found`; 409 `crm-buyer-ambiguous-customer-match`; 502 |

Note: with `Crm:Provider=Http` the three `units/...` routes resolve `UnimplementedCrmHttpGateway` (fails closed -> 502). Only `buyers` has a real HTTP gateway (`CrmBuyerHttpGateway`, `GET {Crm:BaseUrl}/TicketingSystem/GetBuyerByPhone`).

### VerificationSessionsController - `api/verification-sessions`
| Method | Route | Headers/Body | Success | Errors |
|---|---|---|---|---|
| POST | `/api/verification-sessions` | header `Idempotency-Key?`; body `{unitReferenceId*:int, contactReferenceId*:int, confirmed*:true, verificationMethod*: ManualAgentConfirmation\|AuthenticatedDigitalUser\|FaceToFaceDocumentCheck\|Other}` | 201 `VerificationSessionResponseDto` + Location | 400 `confirmed` false / bad method / `"Otp"` -> `otp-requires-challenge`; 404 `unit-or-contact-not-found` |
| GET | `/api/verification-sessions/{id:guid}` | - | 200 session (owner only) | 403 (empty body, not owner); 404 |

Known defect: the method check accepts numeric strings (see audit F-5): `"3"` is parsed to `Otp` and is not caught by the string comparison.

Sample: `POST /api/verification-sessions` `{"unitReferenceId":12,"contactReferenceId":34,"confirmed":true,"verificationMethod":"ManualAgentConfirmation"}`.

### CustomerHistoryController - `api/customers` (shares prefix with CustomersController)
| Method | Route | Auth | Params | Success |
|---|---|---|---|---|
| GET | `/api/customers/search` | CustomerVerification | `phoneNumber*` | 200 `CustomerSearchResultDto` (400 blank) |
| GET | `/api/customers/crm/{crmCustomerId:int}/ticket-history` | AuthenticatedStaff | `limit?` (default 5, max 50), `unitNumber?`, `orderActiveFirst` | 200 `CustomerHistoryDto` |
| GET | `/api/customers/external/{source}/{externalCustomerId}/ticket-history` | AuthenticatedStaff | same | 200 |
| GET | `/api/customers/lookup/ticket-history` | CustomerVerification | `phoneNumber*, crmCustomerId*(>0), limit, unitNumber, orderActiveFirst` | 200; 400 (empty body); 404 (empty body) |

### CustomersController - `api/customers` (AuthenticatedStaff)
| GET | `/api/customers` | query `CustomerDirectoryListRequestDto`: `search, verificationSource (Crm/Unverified/Pact/Tasleeh), departmentId, openOnly, page=1, pageSize=25` | 200 `CustomerDirectoryListResultDto` |
| GET | `/api/customers/profile/{customerKey}` | key forms `crm:{id}`, `ext:{source}:{id}`, `phone:{number}` | 200 `CustomerDirectoryProfileDto`; 400 / 404 ProblemDetails |

### TicketsController - `api/tickets` (class: AuthenticatedStaff; per-action role checks inside app services; ticket not visible -> 404, role refusal -> empty 403)
| Method | Route | Extra auth | Body / query | Success | Errors |
|---|---|---|---|---|---|
| POST | `/api/tickets` | CustomerVerification | `CreateTicketRequestDto`: `intakeRecordId*, requestSummary*`, `unitReferenceId?`+`contactReferenceId?` (both or neither), `categoryId?` (or `departmentId`), `priorityId?` (only with category), CRM buyer set `crmBuyerCustomerId/LeadId/UnitId/ProjectId` (all or none) + `crmBuyerCustomerName/ProjectName/UnitNumber`, `manualProjectName/manualUnitNumber`, `customerVerificationSource`, `externalCustomerId/UnitId/Name/Email`, `requestTypeId?`, `genesysContext?` | 201 `TicketResponseDto` | 404 intake/unit/contact/category/priority/inactive department; 409 intake already linked, ticket-number-collision; 422 unit-or-contact mismatch, category-department-mismatch, priority-requires-category, CRM buyer set mismatch, buyer+manual both, buyer+external both, external source missing, request-type not found / workflow not published / department mismatch, genesys conversation id required |
| GET | `/api/tickets` | - | query `TicketListRequestDto`: `departmentId, categoryId, priorityId(1-4), ticketStatus, verificationStatus, ownerEmployeeId, search, sortBy, sortDir, page, pageSize, channelId, requestTypeId, activeOnly, inDepartmentQueue, slaBreached, dueToday, backlogAge, pendingApproval, createdFrom, createdTo` | 200 paged list | - |
| GET | `/api/tickets/{id}` | - | - | 200 detail | 404 |
| GET | `/api/tickets/{id}/customer-history` | - | `limit?` | 200 | 404 |
| GET | `/api/tickets/{id}/customer-profile` | - | - | 200 (`status`: NotCrmVerified/Found/CrmUnavailable/AmbiguousCustomerMatch/NotFoundInCrm/NoPhoneOnRecord) | 404 |
| GET | `/api/tickets/{id}/interactions` | - | - | 200 interactions + transcripts | 404 |
| POST | `/api/tickets/{id}/classification` | - | `{categoryId*, priorityId*, requestTypeId?, rowVersion*}` | 200 | 400; 404; 409 stale; 422 already-classified / category-not-found / category-department-mismatch / priority-not-found |
| POST | `/api/tickets/{id}/assignment` | - | `{assignedEmployeeId*, rowVersion*}` | 200 | 404; 409; 422 employee-not-in-department / ticket-closed |
| POST | `/api/tickets/{id}/transfer` | - | `{targetDepartmentId*, reason*, rowVersion*, assignToEmployeeId?}` | 200 | 404 (incl. department-inactive); 409; 422 already-in-target-department / employee-not-in-department / ticket-closed |
| POST | `/api/tickets/{id}/status` | - | `{newStatus*: InProgress\|PendingCustomer, rowVersion*, pendingReason? (required for PendingCustomer)}` | 200 | 409; 422 invalid-status-transition / ticket-not-assigned / pending-reason-required / not-allowed-for-request-type / disabled-by-department-settings |
| POST | `/api/tickets/{id}/resolution` | Department Employee / Head (service) | `{resolutionOutcome*: Resolved\|Cancelled\|Rejected\|Duplicate, resolutionNote*, reasonCode?, duplicateOfTicketId? (Duplicate), rowVersion*}` | 200 | 409; 422 not-eligible-for-resolution / duplicate-chain-not-allowed / ticket-closed |
| POST | `/api/tickets/{id}/close` | CS Agent/Supervisor/Manager (service) | `{rowVersion*}` | 200 | 409 `not-yet-resolved` or stale; 422 ticket-closed |
| POST | `/api/tickets/{id}/reopen` | CS Agent/Supervisor/Manager (+SysAdmin) | `{reason*, targetDepartmentId*, rowVersion*}` | 200 | 403; 400/422 reopen-reason-required, target-department-required; 404; 409; 422 not-eligible-for-reopen / resolution-outcome-not-reopenable / reopen-window-expired |
| GET | `/api/tickets/{id}/approvals` | - | - | 200 view | 404 |
| GET | `/api/tickets/{id}/history` | - | - | 200 lifecycle history | 404 |
| POST | `/api/tickets/{id}/approvals` | operational actors | `{approvalType*, comment?}` | 200 | 400 invalid-approval-input; 404; 409 duplicate-active-approval; 422 approval-not-configured / ticket-closed |
| POST | `/api/tickets/{id}/approvals/{approvalId:long}/decision` | configured approval target | `{decision*: Approve\|Reject, comment? (required for Reject)}` | 200 | 409 approval-already-decided; 422 rejection-reason-required / reopen-not-eligible |
| POST | `/api/tickets/{id}/approvals/{approvalId:long}/cancellation` | operational actors | `{comment?}` | 200 | 404; 409 |
| POST | `/api/tickets/{id}/workflow-events` | operational actors | `{eventType*: PrerequisitesCompleted\|MaintenanceRequired\|MaintenanceNotRequired\|MaintenanceCompleted, note?}` | 204 | 400; 404; 409 workflow-event-already-recorded; 422 workflow-event-not-applicable |
| POST | `/api/tickets/{id}/reconciliation` | CustomerVerification | `{verificationSessionId*, rowVersion*}` | 200 | 404 (ticket/session); 403 session not owned (empty); 409 stale / already verified / session consumed; 410 session expired; 422 unit mismatch / session not confirmed |
| POST | `/api/tickets/{id}/notes` | - | `{noteText*}` | 201 `TicketNoteResponseDto` | 400; 404 |
| GET | `/api/tickets/{id}/notes` | - | `page` (0->1), `pageSize` (0->50) | 200 | 404 |

### TicketSlaController - `api/tickets/{ticketId:long}` (AuthenticatedStaff)
| GET | `.../sla` | - | 200 `TicketSlaSummaryResponseDto` | 403, 404 |
| POST | `.../sla/first-response` | `{source*: Manual\|GenesysCallAnswer, occurredAtUtc?, rowVersion*}` | 200 | 400; 403; 404; 409 first-response-already-recorded / stale; 422 ticket-closed |
| POST | `.../escalations` | `{level*:1-4, triggerType*: ManualFlag\|ManualLevel4, note?, rowVersion*}` (Level 4 requires ManualLevel4 and CS Manager/GM) | 201 `TicketEscalationResponseDto` | 400; 403; 404; 409; 422 escalation-level-not-an-advance / -trigger-mismatch / ticket-closed |
| GET | `.../escalations` | - | 200 list | 403, 404 |

### PriorityDowngradeRequestsController (policy AuthenticatedStaff; decisions DepartmentHeadOrAbove + service-level checks)
| Method | Route | Body | Success | Errors |
|---|---|---|---|---|
| POST | `/api/tickets/{ticketId:long}/sla/priority-downgrade-requests` | `{newPriorityId*, reason*}` | 201 request (`Status: Pending`, `ExpiresAtUtc`); ticket priority/SLA unchanged | 400; 403; 404; 409 downgrade-request-already-pending; 422 not a decrease |
| GET | `/api/tickets/{ticketId:long}/sla/priority-downgrade-requests` | – | 200 history | 403, 404 |
| GET | `/api/priority-downgrade-requests/pending` | – | 200 inbox for the caller's decidable requests | 403 |
| POST | `/api/priority-downgrade-requests/{requestId:long}/approve` | `{rowVersion}` | 200 request + new SLA period (`ChangeReason=Downgrade`) | 403 (not Dept Head of current dept / self-approval); 404; 409 not-pending / stale priority / concurrency; 410 expired; 422 ticket final |
| POST | `/api/priority-downgrade-requests/{requestId:long}/reject` | `{decisionNote*}` | 200 request (`Rejected`), priority unchanged | 400; 403; 404; 409 |

A priority decrease through any other route returns 403 `downgrade-requires-approval`. Service-identity accounts (see [03 §10](03-roles-and-permissions.md)) receive 403 on every route in this controller.

### PendingCustomerInteractionsController - `api/pending-customer-interactions` (AuthenticatedStaff; department rule inside `AgentHandoffAppService`)
| Method | Route | Params | Success | Errors |
|---|---|---|---|---|
| GET | `/api/pending-customer-interactions` | `departmentId, channelId, assignedEmployeeId, unassignedOnly, includeResolved, page (0->1), pageSize (0->50, 1-200)` | 200 `AgentHandoffListResultDto` | - |
| POST | `.../{handoffId:long}/start` | - | 200 `AgentHandoffDto` | 403 `forbidden`; 404; 409 already-resolved / already-claimed / ticket-closed / concurrency-conflict; 422 employee-not-in-department |
| POST | `.../{handoffId:long}/complete` | `{resolutionNote?}` | 200 | 403; 404; 409 |
| POST | `.../{handoffId:long}/cancel` | `{reason*}` | 200 | 403; 404; 409; 422 pending-interaction-reason-required |

---

## 3. Administration (all `SystemAdministrator`, base `AdminControllerBase`)

Error mapping (`AdminControllerBase.cs`): NotFound -> 404 (no body); ValidationFailed -> 400 ValidationProblem; Conflict -> 409 `administration-conflict`; password policy -> 422 `password-policy-violation`.

| Controller | Method | Route | Body | Success | Extra errors |
|---|---|---|---|---|---|
| AdminChannels | GET | `/api/admin/channels?includeInactive=true` | - | 200 list | |
| | GET | `/api/admin/channels/{channelId:int(1-255)}` | - | 200 | 404 |
| | POST | `/api/admin/channels` | `{name, code, requiresPhone, isGenesysEnabled, displayOrder, isActive=true}` | 201 | 400 |
| | PUT | `/api/admin/channels/{channelId}` | same | 200 | 400, 404 |
| | PATCH | `/api/admin/channels/{channelId}/activation` | `{isActive, reason?}` | 200 | 404 |
| AdminDepartments | GET | `/api/admin/departments?includeInactive=true` | - | 200 | |
| | GET | `/api/admin/departments/{departmentId}` | - | 200 | 404 |
| | POST | `/api/admin/departments` | `{name, code}` | 201 | 400 |
| | PUT | `/api/admin/departments/{id}` | `{name, code}` | 200 | 400, 404 |
| | PATCH | `/api/admin/departments/{id}/activation` | `{isActive, reason?}` | 200 | 404 |
| | POST | `/api/admin/departments/{id}/members` | `{employeeId, isPrimary}` | 200 | 400, 404, 409 |
| | DELETE | `/api/admin/departments/{id}/members/{employeeId:guid}` | - | 200 | 404, 409 |
| AdminGenesys | GET | `/api/admin/genesys/queue-mappings?includeInactive=true` | - | 200 | |
| | POST | `/api/admin/genesys/queue-mappings` | `{queueId, queueName?, departmentId, isActive=true}` | 201 | 400 |
| | PUT | `/api/admin/genesys/queue-mappings/{genesysQueueMappingId:int}` | same (queueId ignored) | 200 | 400, 404 |
| AdminRequestTypes | GET | `/api/admin/request-types?departmentId&includeInactive=true` | - | 200 | |
| | GET | `/api/admin/request-types/{id}` | - | 200 | 404 |
| | POST | `/api/admin/request-types` | `{departmentId, name, workflowId, defaultPriorityId, allowAgentPriorityChange, allowPendingCustomer, allowPendingInternal, allowReopen, requiredFieldsJson?}` | 201 | 400 |
| | PUT | `/api/admin/request-types/{id}` | same | 200 | 400, 404, 409 |
| | PATCH | `/api/admin/request-types/{id}/activation` | `{isActive, reason?}` | 200 | 404, 409 |
| | PUT | `/api/admin/request-types/{id}/assignment-rule` | `{mode, primaryEmployeeId?, memberEmployeeIds?, teamName?, isActive=true}` | 200 | 400, 404 |
| | PUT | `/api/admin/request-types/{id}/approval-requirements/{approvalType}` | `{targetKind, targetDepartmentId?, targetRoleName?, targetEmployeeId?, blocksWorkUntilApproved=true, isActive=true}` | 200 | 400, 404 |
| | PUT | `/api/admin/request-types/{id}/sla-policies/{priorityId:byte}` | `{trigger, unit, firstResponseTargetValue?, firstResponseMaximumValue?, resolutionTargetValue?, resolutionMaximumValue?, isImmediate, clockBasis?, pausesOnPendingCustomer?, pausesOnPendingInternal?, warningThresholdPercent?, isActive=true}` | 200 | 400, 404 |
| AdminSla | GET | `/api/admin/sla/configuration` | - | 200 `SlaConfigurationDto` | |
| AdminUsers | GET | `/api/admin/users?search&includeInactive=false&page=1&pageSize=25` | - | 200 | |
| | GET | `/api/admin/users/{employeeId:guid}` | - | 200 | 404 |
| | POST | `/api/admin/users` | `{userName, email?, displayName, isGeynessStaff, initialPassword, roles[], primaryDepartmentId?}` | 201 | 400, 422 password policy |
| | PUT | `/api/admin/users/{id}/profile` | `{displayName, email?, isGeynessStaff}` | 200 | 400, 404 |
| | PATCH | `/api/admin/users/{id}/activation` | `{isActive, reason?}` | 200 | 404, 409 |
| | PUT | `/api/admin/users/{id}/roles` | `{roles[]}` | 200 | 400, 404, 409 |
| | POST | `/api/admin/users/{id}/departments` | `{departmentId, isPrimary}` | 200 | 400, 404, 409 |
| | DELETE | `/api/admin/users/{id}/departments/{departmentId:int}` | - | 200 | 404, 409 |
| | POST | `/api/admin/users/{id}/password` | `{newPassword, reason?}` | 204 | 404, 422 |
| AdminWorkflows | GET | `/api/admin/workflows/catalog` | - | 200 | |
| | GET | `/api/admin/workflows?includeInactive=true` | - | 200 | |
| | GET | `/api/admin/workflows/{workflowId}` | - | 200 | 404 |
| | POST | `/api/admin/workflows` | `{name, description?}` | 201 | 400 |
| | PUT | `/api/admin/workflows/{id}` | `{name, description?}` | 200 | 400, 404 |
| | PATCH | `/api/admin/workflows/{id}/activation` | `{isActive, reason?}` | 200 | 404, 409 |
| | POST | `/api/admin/workflows/{id}/versions` | - | 201 | 404, 409 draft exists |
| | GET | `/api/admin/workflows/versions/{versionId}` | - | 200 | 404 |
| | PUT | `/api/admin/workflows/versions/{versionId}/settings` | `{name, description?, allowsPendingCustomer, allowsPendingInternal, requiresApproval}` | 200 | 400, 404, 409 |
| | POST | `.../versions/{versionId}/steps` | `{name, kind, isOptional, approvalType?}` | 200 | 400, 404, 409 |
| | PUT | `.../versions/{versionId}/steps/{stepId}` | same | 200 | 400, 404, 409 |
| | DELETE | `.../versions/{versionId}/steps/{stepId}` | - | 200 | 404, 409 |
| | POST | `.../versions/{versionId}/steps/{stepId}/move` | `{direction}` | 200 | 404, 409 |
| | PUT | `.../versions/{versionId}/steps/{stepId}/transitions` | `{outcome, targetStepId?}` | 200 | 400, 404, 409 |
| | POST | `.../versions/{versionId}/publish` | - | 200 | 400, 404, 409 |
| | DELETE | `.../versions/{versionId}` | - | 204 | 404, 409 |

---

## 4. Collections

All `AuthenticatedStaff` at the HTTP layer; real authorization is `CollectionsAuthorizationService` (see doc 10). Errors are ProblemDetails with `type=https://tigercs.internal/problems/collections/<code>` and extension members `code` (and `message` on most). Codes: `Forbidden` 403, `InvalidRequest` 400, `CollectionsDisabled` 503, `FinanceUnavailable` 503, `AccountNotFound` 404, `ReminderNotFound` 404, `CandidateChanged` 409 (+`replacementCandidate`), `IdempotencyConflict` 409, `ReminderSuppressed` 409, `NoEligibleContact` 422, `ChannelNotEnabled` 422, `CustomerNotMapped` 422. (`PactReceivablesController` and `CollectionsCampaignsController` only emit Forbidden/InvalidRequest/CollectionsDisabled/FinanceUnavailable.)

### PactReceivablesController - `api/collections/receivables/customers`
| GET | `/api/collections/receivables/customers` | query: `companyId?` (4 or 32), `status` all\|due\|overdue (default all), `search?` (<=200), `page=1`, `pageSize=25` (1-100), `year?`+`month?` (together; 2000-2100 / 1-12; default current Dubai month) | 200 `PactReceivableCustomersDto` | 400, 403, 503 |

### CollectionsCampaignsController - `api/collections/campaigns` (`ResponseCache NoStore`)
| GET | `/api/collections/campaigns/preview` | `stage*` (OverdueReminder\|CurrentMonthReminder\|FollowUpReminder\|LegalNotice\|LegalReferral), `businessDate?`, `companyId?`, `search?`, `page=1`, `pageSize=25`, `dateFrom?`, `dateTo?` | 200 `CollectionsCampaignPreviewDto` | 400, 403, 503 |
| GET | `/api/collections/campaigns/export` | `stage*`, `mode*` (review\|genesys), `businessDate?`, `companyId?`, `search?`, `dateFrom?`, `dateTo?` | 200 `{fileName, csv, rowCount}` (CSV text inside JSON) | 400 (incl. genesys-mode refusals), 403 (needs reminder-send grant), 503 |

### CollectionsController (`api/collections`, Web prefix) and GenesysCollectionsController (`api/genesys/collections`, Genesys prefix)
Both inherit `CollectionsControllerBase`; identical actions below. Only difference: default read deadline (`WebReadDeadlineSeconds=60` vs `GenesysReadDeadlineSeconds=22`) and the extra Web-only route.

| Method | Route (relative) | Params | Success | Errors |
|---|---|---|---|---|
| GET | `customers/by-key/{customerKey}/payment-summary` | `includeTransactions=true`; header `X-Collections-Deadline-Seconds?`; key `ext:Pact:{tenantID}` | 200 `CollectionsPaymentSummaryResponseDto` (`mappingStatus` Mapped / NotMapped; per-company fields; `nextPayment`) | 400, 403, 404, 503 |
| GET | `customers/by-key/{customerKey}/payment-transactions` | `companyId*`, `type*` Paid\|Due\|Outstanding (or 1-3; All/4 refused) | 200 `CollectionsPaymentTransactionsResponseDto` | 400, 403, 404, **422 CustomerNotMapped**, 503 |
| GET | `customers/{crmCustomerId}/outstanding` | `accountId?`, `unitId?`, `cursor?`, `pageSize` 1-100 (50) | 200 `CollectionsOutstandingResponseDto` | 400, 403, 404, 503 (always 503 while `CollectionsSource:Provider=Unavailable`) |
| GET | `customers/{crmCustomerId}/payments` | `view` instalments (default) \| history, `accountId?`, `unitId?`, `fromDate?`, `toDate?` (history), `cursor?`, `pageSize?` | 200 instalments or history DTO | 400, 403, 404, 503 |
| GET | `reminders/candidates` | `reminderType*` OverdueMonthly\|CurrentMonth\|MonthEndFollowUp, `businessDate?` (must be today), `crmCustomerId?`, `accountId?`, `cursor?`, `pageSize?` | 200 `CollectionsReminderCandidatesResponseDto` | 400, 403, 503 |
| POST | `reminders` | header `Idempotency-Key?`; body `{candidateId*, channels*[VoiceBot\|Sms\|Email], language? en\|ar}` | 202 `CollectionsReminderJobDto` (replay: 200) | 400, 403, 409 CandidateChanged / IdempotencyConflict, 422 NoEligibleContact / ChannelNotEnabled, 503 |
| POST | `reminders/{reminderId}/outcomes` | header `Idempotency-Key?`; body `{eventId*, channel*, providerMessageId?, conversationId?, occurredAtUtc?, deliveryStatus?, customerResponded, customerIntent?, requiresHumanFollowUp, customerPhone?}` | 200 (or 202 `ticketResult: Pending`) `RecordReminderOutcomeResponseDto` | 400, 403, 404 ReminderNotFound, 409 IdempotencyConflict / ReminderSuppressed, 503 |
| GET | `customers/{crmCustomerId}/reminders` | `accountId?`, `cursor?`, `pageSize?` | 200 `CollectionsReminderHistoryResponseDto` | 400, 403, 503 |
| GET | **Web only:** `/api/collections/customer-lookup/payment-summary` | `phoneNumber*`, `customerKey*`; policy `CustomerVerification` | 200 summary | 400, 403, 404, 503 |

Sample: `curl -H "Authorization: Bearer $T" "$API/api/collections/receivables/customers?companyId=4&status=overdue&page=1&pageSize=25"`.

---

## 5. Genesys-facing routes (policy `CustomerVerification` unless noted)

Callers reach these via TigerGroupWeb as an integration service account that holds the CS Agent role. Public URL = `https://tigergroup.ae` + path (unverified).

### GenesysController - `api/genesys`
| Method | Route | Body / query | Success | Errors |
|---|---|---|---|---|
| POST | `/api/genesys/tickets` | `GenesysInquiryRequest`: `conversationId*, channel*` (Phone\|LiveChat\|WebsiteChat\|WebMessaging\|WhatsApp\|Instagram\|Facebook\|SocialMedia), `interactionId, participantId, communicationId, direction, customerPhone, customerName, customerEmail, calledNumber, queueId, queueName, agentId, agentName, startedAtUtc, departmentId, departmentCode, towerName, unitNumber, subject` | 201 `{outcome:TicketCreated, conversationId, ticketId, ticketNumber}`; replay 200 `AlreadyIngested` | 400 (channel / `genesys-conversation-id-required`); 422 department-not-resolved / channel-not-configured / ticket-creation-failed; 503 `genesys-integration-disabled` |
| PATCH | `/api/genesys/tickets/{ticketId:long}` | `GenesysTicketUpdateRequest`: `conversationId*, agentId, agentName, ended{endedAtUtc,endReason,transcript[{sender*,sentAtUtc*,body*,senderName,senderId,externalMessageId}]}, handoff{required?,agentAvailable,mode,reason,trigger,workItemId,assignedAgentId}, startedAtUtc, routing{queueId,queueName,agentId,agentName}, customerConfirmation{confirmedResolved*:true,confirmedAtUtc,note}, awaitingCustomerReply?` | 200 `GenesysTicketUpdateResponse` | 400 (blank conversation, invalid transcript/handoff mode/trigger/reason/confirmation); 404 ticket / conversation; 409 conversation belongs to another ticket; 422 `genesys-no-open-handoff`; 503 |
| GET | `/api/genesys/customers/lookup` | `phoneNumber*` | 200 `GenesysCustomerLookupResultDto` (`found:false` is 200) | 400; 503 |
| POST | `/api/genesys/customers/unit-details` | `{customerReference*, phoneNumber*, unitId?}` | 200 `UnitSelectionRequired` (eligibleUnits) or `UnitDetails` | 400; 403 `CUSTOMER_NOT_VERIFIED` / `UNIT_NOT_ELIGIBLE`; 409 `CUSTOMER_AMBIGUOUS`; 502 `CRM_UNAVAILABLE`; 503 |
| POST | `/api/genesys/agent-context` | `{genesysUserId*, agentEmail?, conversationId?}` | 200 `GenesysAgentContextResponse` | 400; 403 `GENESYS_AGENT_NOT_MAPPED` / `_INACTIVE`; 404 conversation; 503 |
| POST | `/api/genesys/screen-pop` | `{genesysUserId*, conversationId?, ticketId?, customerPhone?}` | 200 `{launchUrl, expiresAtUtc, expiresInSeconds:3600, targetPath, ticketId?}` | 400; 403 mapping codes; 503 disabled or `GENESYS_SCREEN_POP_NOT_CONFIGURED` |

### GenesysVerificationController - `api/genesys/verification` (email OTP; new)
| Method | Route | Body | Success | Errors (code) |
|---|---|---|---|---|
| POST | `/api/genesys/verification/buyer-lookup` | `{phoneNumber*}` | 200 `CustomerOtpResult` status `Found` (units, masked email) | 400; 404 `CUSTOMER_NOT_FOUND`; 409 `CUSTOMER_AMBIGUOUS`; 502 `CRM_AUTHENTICATION_FAILED`/`CRM_INVALID_RESPONSE`; 503 `CRM_UNAVAILABLE`/`DOCUMENT_COPY_DISABLED` |
| POST | `/api/genesys/verification/otp/send` | `{phoneNumber*, crmUnitId?, channel? Email\|Sms, language? en\|ar}` | 200 `CodeSent` (+`channel`) / `AlreadySent` / `UnitSelectionRequired` | 400; 403 `UNIT_NOT_OWNED`; 404; 409; 422 `NO_EMAIL_ON_RECORD` / `NO_MOBILE_ON_RECORD`; 503 `OTP_SMS_NOT_CONFIGURED`; 504 `OTP_DELIVERY_UNCONFIRMED` (SMS, see `Sms-Verification-Channel.md`); 429 `OTP_RATE_LIMITED`/`OTP_RESEND_LIMIT_REACHED` (+`Retry-After`); 502 `OTP_DELIVERY_FAILED`; 503 (also when `EmailNotifications:Enabled=false`) |
| POST | `/api/genesys/verification/otp/resend` | `{challengeId*}` | 200 `CodeSent` | 404 `OTP_CHALLENGE_NOT_FOUND`; 410 `OTP_EXPIRED`; 423 `OTP_LOCKED`; 429 `OTP_RESEND_TOO_SOON`/limit; 502 |
| POST | `/api/genesys/verification/otp/verify` | `{challengeId*, code*}` (6 digits) | 200 `Verified` + `session` | 400 `OTP_INVALID` (`attemptsRemaining`); 404; 409 `OTP_ALREADY_USED`; 410; 423 |

ProblemDetails extensions: `code`, `outcome`, `challengeId`, `maskedDestination`, `attemptsRemaining`, `resendAvailableAtUtc`.

### GenesysDocumentsController - `api/genesys/documents`
| POST | `/api/genesys/documents/send-copy` | header `Idempotency-Key*`; body `{verificationSessionId*, documentType* Contract\|ReservationForm\|UnitLayout\|RegistrationReceipt, crmUnitId?, recordId?, deliveryChannel? (Email default)}` | 200 `Sent` (replay `duplicate:true`) or `SelectionRequired` (`choices`); 202 `Queued` | 400 `INVALID_REQUEST`; 403 `VERIFICATION_FAILED` / `RECORD_OWNERSHIP_MISMATCH`; 404 `DOCUMENT_NOT_FOUND`; 409 `IDEMPOTENCY_KEY_REUSED`; 422 `DELIVERY_DESTINATION_UNAVAILABLE`; 501 `DELIVERY_CHANNEL_NOT_INTEGRATED`; 502 `DELIVERY_FAILED`/`DOCUMENT_TOO_LARGE`; 503 `DOCUMENT_SOURCE_UNAVAILABLE`/`DOCUMENT_COPY_DISABLED` |

Sample:
```
POST /api/genesys/documents/send-copy
Idempotency-Key: conv-123-contract-1
{"verificationSessionId":"<guid from otp/verify>","documentType":"Contract"}
```

Coverage check: 31 controller/handler files under `src/TigerCS.Api/Controllers`: AdminChannels, AdminControllerBase (no routes), AdminDepartments, AdminGenesys, AdminRequestTypes, AdminSla, AdminUsers, AdminWorkflows, Auth, Categories, Channels, CollectionsCampaigns, Crm, CustomerHistory, CustomerLookup, Customers, Dashboard, Departments, GenesysCollections (also hosts `CollectionsController` and `CollectionsControllerBase`), GenesysContracts (DTOs only), Genesys, GenesysDocuments, GenesysVerification, IntakeRecords, PactReceivables, PendingCustomerInteractions, Reports, RequestTypes, Roles, TicketSla, Tickets, Users, VerificationSessions.
