/*
================================================================================
 Identify_TigerCS_PROD_GenesysIntegrationAccount_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: the live assignment backfill needs the verified EmployeeId of every service / integration account (Msg 50806). Both configuration lists are empty in the committed
      appsettings.Production.json (that is by design - Production supplies them as API-pool environment variables, so an empty FILE proves nothing about the LIVE pool).
      This script finds the real id of the Genesys integration account in the database. It does not guess, create or change anything.

 HOW THE ACCOUNT EXISTS (from the code and docs):
   * It is an ordinary TigerCS user: one AspNetUsers row (login) whose Id is also the Employees.EmployeeId (the two tables share the key).  No code creates it; an
     administrator created it by hand.  It holds the role CS Agent (a "CS Agent by permission set") and is NOT a person.
   * It is the account TigerGroupWeb signs in with (TigerGroupWeb setting  Ticketing:Username / Ticketing:Password) when it forwards Genesys data actions to TigerCS.  The
     name used in the tests and docs is  genesys.integration , but the real user name is whatever was configured in TigerGroupWeb - so the name is a LOOK-UP KEY here, not proof.
   * Tickets.CreatedByEmployeeId is "the account that called ticket creation - for a Genesys inquiry that is the integration account" (Ticket.RecordCreator).  That is the
     independent corroboration used below.
   * It becomes a service identity ONLY when its id is listed in  Authorization:ServiceIdentity:EmployeeIds  or  Collections:Authorization:IntegrationEmployeeIds.
     Until then the application does not restrict it to the integration surface and the backfill / rotation could deal it tickets.

 HOW TO USE
   1. Optionally add the real TigerGroupWeb user name(s) to @KnownUserNames below (ask whoever owns TigerGroupWeb's Ticketing:Username).  'genesys.integration' is pre-filled.
   2. Run.  Read result 1 (the verdict), then 2-6.
   3. A row is CONFIRMED_INTEGRATION_ACCOUNT only when TWO independent facts agree: the user name matches AND the account really creates Genesys tickets without ever
      handling a conversation as a person.  Anything weaker is shown as a CANDIDATE with the reason, never promoted.
   4. Take the ids from result 1b to the owner of the API-pool configuration for sign-off; they go into @ServiceIdentity of the backfill only after that.

 NOTHING IS CHANGED: SELECT and table variables only; READ UNCOMMITTED; no customer name / phone / e-mail is selected.  Safe from a db_datareader login.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this script is for [' + @ExpectedDb + N'] only.';
    THROW 50710, @m, 1;
END;

-- ------------------------------------------------ LOOK-UP KEYS (not proof) --------------------------------------------------------------
DECLARE @KnownUserNames TABLE (UserName nvarchar(256) NOT NULL);
INSERT @KnownUserNames VALUES (N'genesys.integration');
-- INSERT @KnownUserNames VALUES (N'<the Ticketing:Username TigerGroupWeb signs in with>');
-- ----------------------------------------------------------------------------------------------------------------------------------------

DECLARE @Now datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @CcId int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);

-- 1) Accounts found by name (user name, normalised user name or e-mail), case-insensitive.
DECLARE @ByName TABLE (UserId uniqueidentifier PRIMARY KEY, MatchedOn nvarchar(256) NOT NULL);
INSERT @ByName (UserId, MatchedOn)
SELECT u.Id, MIN(k.UserName)
FROM dbo.AspNetUsers u
JOIN @KnownUserNames k ON u.NormalizedUserName = UPPER(k.UserName) OR u.NormalizedEmail = UPPER(k.UserName) OR u.UserName = k.UserName
GROUP BY u.Id;

-- 2) Per-account evidence for: every account found by name, and every account that creates Genesys tickets.
DECLARE @Ev TABLE (
    UserId uniqueidentifier PRIMARY KEY, UserName nvarchar(256) NULL, Email nvarchar(256) NULL, NameMatch bit NOT NULL, MatchedOn nvarchar(256) NULL,
    HasEmployeeRow bit NOT NULL, IsActive bit NOT NULL, IsLocked bit NOT NULL, DisplayName nvarchar(256) NULL, Roles nvarchar(400) NULL,
    HasGenesysUserMapping bit NOT NULL, InCallCenter bit NOT NULL,
    GenesysTicketsCreated int NOT NULL, FirstCreatedUtc datetime2 NULL, LastCreatedUtc datetime2 NULL, DepartmentsCreatedFor int NOT NULL,
    InteractionsHandled int NOT NULL, GenesysAuditRows int NOT NULL, ActiveTicketsOwned int NOT NULL, TicketsEverAssignedToIt int NOT NULL,
    InRotationPool bit NOT NULL, Class varchar(70) NOT NULL);

INSERT @Ev (UserId, UserName, Email, NameMatch, MatchedOn, HasEmployeeRow, IsActive, IsLocked, DisplayName, Roles, HasGenesysUserMapping, InCallCenter,
            GenesysTicketsCreated, FirstCreatedUtc, LastCreatedUtc, DepartmentsCreatedFor, InteractionsHandled, GenesysAuditRows, ActiveTicketsOwned, TicketsEverAssignedToIt,
            InRotationPool, Class)
SELECT u.Id, u.UserName, u.Email,
       CASE WHEN n.UserId IS NOT NULL THEN 1 ELSE 0 END, n.MatchedOn,
       CASE WHEN e.EmployeeId IS NOT NULL THEN 1 ELSE 0 END,
       CASE WHEN e.EmployeeId IS NOT NULL AND e.DeactivatedAtUtc IS NULL THEN 1 ELSE 0 END,
       CASE WHEN u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > @Now THEN 1 ELSE 0 END,
       e.DisplayName,
       (SELECT STRING_AGG(r.Name, N', ') FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = u.Id),
       CASE WHEN NULLIF(LTRIM(RTRIM(u.GenesysUserId)), N'') IS NOT NULL THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = u.Id AND a.DepartmentId = @CcId) THEN 1 ELSE 0 END,
       c.Cnt, c.FirstUtc, c.LastUtc, c.Depts,
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.HandledByUserId = u.Id)
         + (SELECT COUNT(*) FROM dbo.TicketInteractionHandlers h WHERE h.HandledByUserId = u.Id),
       (SELECT COUNT(*) FROM dbo.AuditEntries au WHERE au.ActorEmployeeId = u.Id AND au.Action LIKE N'Genesys%'),
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = u.Id AND t.TicketStatus BETWEEN 1 AND 4),
       (SELECT COUNT(*) FROM dbo.TicketAssignments ta WHERE ta.AssignedEmployeeId = u.Id),
       CASE WHEN e.EmployeeId IS NOT NULL AND e.DeactivatedAtUtc IS NULL AND @CcId IS NOT NULL
                 AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = u.Id AND a.DepartmentId = @CcId)
                 AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > @Now)
                 AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = u.Id AND r.Name = N'CS Agent')
            THEN 1 ELSE 0 END,
       'PENDING'
FROM dbo.AspNetUsers u
LEFT JOIN dbo.Employees e ON e.EmployeeId = u.Id
LEFT JOIN @ByName n ON n.UserId = u.Id
OUTER APPLY (SELECT COUNT(*) AS Cnt, MIN(t.CreatedAtUtc) AS FirstUtc, MAX(t.CreatedAtUtc) AS LastUtc, COUNT(DISTINCT t.CurrentDepartmentId) AS Depts
             FROM dbo.Tickets t
             WHERE t.CreatedByEmployeeId = u.Id AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2)) c
WHERE n.UserId IS NOT NULL OR c.Cnt > 0;

UPDATE @Ev SET Class =
    CASE WHEN NameMatch = 1 AND GenesysTicketsCreated > 0 AND InteractionsHandled = 0 AND HasEmployeeRow = 1
              THEN 'CONFIRMED_INTEGRATION_ACCOUNT'      -- name matches AND it really creates Genesys tickets AND no person ever handled a conversation as it
         WHEN NameMatch = 1 AND GenesysTicketsCreated > 0
              THEN 'CANDIDATE_NAME_MATCH_BUT_HANDLES_CONVERSATIONS'
         WHEN NameMatch = 1
              THEN 'CANDIDATE_NAME_ONLY_NO_USAGE_EVIDENCE'
         WHEN GenesysTicketsCreated > 0 AND InteractionsHandled = 0 AND HasGenesysUserMapping = 0
              THEN 'CANDIDATE_CREATES_GENESYS_TICKETS_OTHER_NAME'   -- possibly the real TigerGroupWeb user name: confirm with its owner, then re-run with that name
         ELSE 'ACCOUNT_THAT_CREATED_A_GENESYS_TICKET_BUT_LOOKS_HUMAN'
    END;

-- ======================================================================================================================
-- RESULT 1  VERDICT
-- ======================================================================================================================
DECLARE @Confirmed int = (SELECT COUNT(*) FROM @Ev WHERE Class = 'CONFIRMED_INTEGRATION_ACCOUNT');
DECLARE @OtherCand int = (SELECT COUNT(*) FROM @Ev WHERE Class = 'CANDIDATE_CREATES_GENESYS_TICKETS_OTHER_NAME');
DECLARE @NameRows int = (SELECT COUNT(*) FROM @ByName);
DECLARE @DupNames int = (SELECT COUNT(*) FROM (SELECT UPPER(UserName) AS N FROM dbo.AspNetUsers WHERE UserName IS NOT NULL GROUP BY UPPER(UserName) HAVING COUNT(*) > 1) d);

SELECT
    CASE
        WHEN @Confirmed >= 1 AND @OtherCand = 0
            THEN 'CONFIRMED: the Genesys integration account was found by name and corroborated by the tickets it creates. Result 1b has its EmployeeId. Get sign-off from the owner of the API-pool configuration before it goes into @ServiceIdentity.'
        WHEN @Confirmed >= 1 AND @OtherCand > 0
            THEN 'CONFIRMED + MORE TO CHECK: at least one account is confirmed, and another account also creates Genesys tickets under a different name (result 3). Find out what it is (TigerGroupWeb Ticketing:Username?) before the list is final.'
        WHEN @NameRows = 0 AND @OtherCand > 0
            THEN 'NOT FOUND BY NAME: no account is called like the keys above, but another account creates Genesys tickets (result 3). That is probably the real integration account. Confirm its user name with the TigerGroupWeb owner, add it to @KnownUserNames and re-run.'
        WHEN @NameRows = 0
            THEN 'NOT FOUND: no account matches the name keys and none creates Genesys tickets. Do not guess. Ask the TigerGroupWeb owner for the Ticketing:Username it signs in with, add it to @KnownUserNames and re-run.'
        ELSE 'NOT CONFIRMED: an account matches by name but the database does not show it creating Genesys tickets (result 2). It may be unused, or a different account does the work (result 3). Do not use it until confirmed.'
    END AS Verdict,
    @NameRows AS AccountsFoundByName, @Confirmed AS ConfirmedIntegrationAccounts, @OtherCand AS OtherAccountsCreatingGenesysTickets,
    CASE WHEN @DupNames > 0 THEN 'WARNING: duplicate user names exist (case-insensitive) - the name alone is ambiguous' ELSE 'no duplicate user names' END AS DuplicateNameCheck;

-- 1b) ids that are CONFIRMED (two independent facts). Paste-ready, but a PROPOSAL for sign-off, not an instruction.
SELECT LOWER(CONVERT(char(36), UserId)) AS ConfirmedEmployeeId, UserName, GenesysTicketsCreated, LastCreatedUtc,
       'INSERT INTO @ServiceIdentity VALUES (''' + LOWER(CONVERT(char(36), UserId)) + ''');' AS ProposedLine_AfterSignOff
FROM @Ev WHERE Class = 'CONFIRMED_INTEGRATION_ACCOUNT' ORDER BY UserId;

-- ======================================================================================================================
-- RESULT 2  accounts found by name
-- ======================================================================================================================
SELECT e.UserId AS EmployeeId_equals_AspNetUsersId, e.UserName, e.Email, e.MatchedOn, e.Class,
       CASE WHEN e.HasEmployeeRow = 1 THEN 'yes' ELSE 'NO EMPLOYEE ROW' END AS EmployeeRow,
       CASE WHEN e.IsActive = 1 THEN 'active' ELSE 'DEACTIVATED / no employee' END AS State,
       CASE WHEN e.IsLocked = 1 THEN 'LOCKED OUT' ELSE 'not locked' END AS Lockout, e.Roles,
       e.GenesysTicketsCreated, e.FirstCreatedUtc, e.LastCreatedUtc, e.DepartmentsCreatedFor,
       e.InteractionsHandled AS InteractionsHandledAsAPerson, e.GenesysAuditRows,
       CASE WHEN e.HasGenesysUserMapping = 1 THEN 'mapped to a Genesys agent (looks like a person)' ELSE 'no Genesys user mapping' END AS GenesysMapping
FROM @Ev e WHERE e.NameMatch = 1 ORDER BY e.UserId;

-- ======================================================================================================================
-- RESULT 3  OTHER accounts that create Genesys tickets  (the integration account(s) under a different name; compare with TigerGroupWeb's Ticketing:Username)
-- ======================================================================================================================
SELECT e.Class, e.UserId AS EmployeeId, e.UserName, e.Email, e.Roles,
       CASE WHEN e.IsActive = 1 THEN 'active' ELSE 'DEACTIVATED / no employee' END AS State,
       e.GenesysTicketsCreated, e.FirstCreatedUtc, e.LastCreatedUtc, e.DepartmentsCreatedFor,
       e.InteractionsHandled AS InteractionsHandledAsAPerson,
       CASE WHEN e.HasGenesysUserMapping = 1 THEN 'mapped to a Genesys agent' ELSE 'no Genesys user mapping' END AS GenesysMapping
FROM @Ev e WHERE e.NameMatch = 0 ORDER BY e.GenesysTicketsCreated DESC, e.UserId;

-- ======================================================================================================================
-- RESULT 4  why the exclusion matters for these accounts: would the backfill / rotation deal them tickets, and have they been dealt tickets before?
-- ======================================================================================================================
SELECT e.UserId AS EmployeeId, e.UserName, e.Class,
       CASE WHEN e.InRotationPool = 1 THEN 'YES - an eligible Call Center CS Agent: unless excluded it WILL be dealt tickets' ELSE 'no - not in the rotation pool' END AS InTheRotationPool,
       e.InCallCenter AS MemberOfCallCenter, e.ActiveTicketsOwned, e.TicketsEverAssignedToIt
FROM @Ev e WHERE e.NameMatch = 1 OR e.Class = 'CANDIDATE_CREATES_GENESYS_TICKETS_OTHER_NAME' ORDER BY e.InRotationPool DESC, e.UserId;

-- ======================================================================================================================
-- RESULT 5  every account that creates Genesys tickets, with the departments it creates for  (ties the account to Collections / other flows)
-- ======================================================================================================================
SELECT t.CreatedByEmployeeId AS EmployeeId, u.UserName, d.Code AS CurrentDepartment, COUNT(*) AS GenesysTickets, MAX(t.CreatedAtUtc) AS LastCreatedUtc
FROM dbo.Tickets t
JOIN dbo.Departments d ON d.DepartmentId = t.CurrentDepartmentId
LEFT JOIN dbo.AspNetUsers u ON u.Id = t.CreatedByEmployeeId
WHERE t.CreatedByEmployeeId IS NOT NULL
  AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2)
GROUP BY t.CreatedByEmployeeId, u.UserName, d.Code
ORDER BY COUNT(*) DESC, t.CreatedByEmployeeId;

-- ======================================================================================================================
-- RESULT 6  roles available and anomalies  (a service account holding more than CS Agent, duplicates, an AspNetUsers row without an Employees row)
-- ======================================================================================================================
SELECT e.UserId AS EmployeeId, e.UserName,
       CASE WHEN e.HasEmployeeRow = 0 THEN 'AspNetUsers row WITHOUT an Employees row - the backfill gate would reject this id (Msg 50807)' END AS Anomaly_NoEmployeeRow,
       CASE WHEN e.Roles IS NOT NULL AND e.Roles <> N'CS Agent' THEN 'holds roles beyond CS Agent: ' + e.Roles END AS Anomaly_ExtraRoles,
       CASE WHEN e.HasGenesysUserMapping = 1 THEN 'has a Genesys user mapping - normally only people do' END AS Anomaly_GenesysMapping
FROM @Ev e
WHERE (e.NameMatch = 1 OR e.Class = 'CANDIDATE_CREATES_GENESYS_TICKETS_OTHER_NAME')
  AND (e.HasEmployeeRow = 0 OR (e.Roles IS NOT NULL AND e.Roles <> N'CS Agent') OR e.HasGenesysUserMapping = 1);
