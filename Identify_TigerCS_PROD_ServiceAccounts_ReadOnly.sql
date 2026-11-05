/*
================================================================================
 Identify_TigerCS_PROD_ServiceAccounts_ReadOnly.sql          (SELECT ONLY - NO WRITES)
 Database : TigerCS_Ticketing_Prod

 WHY: the LIVE assignment backfill stopped with  Msg 50806  "the service-account list is empty".  The backfill must never deal a ticket to a service /
      integration account, and it refuses to run until it is told, explicitly, which employee ids those are.  This script helps you answer that WITHOUT guessing.

 WHAT IS AUTHORITATIVE (and why SQL alone cannot answer):
   The application decides "is this employee a service identity?" in ServiceIdentityRegistry from exactly TWO configuration lists - they are NOT stored in the database:
        Authorization:ServiceIdentity:EmployeeIds
        Collections:Authorization:IntegrationEmployeeIds
   In Production they are API-pool environment variables (Authorization__ServiceIdentity__EmployeeIds__0, __1 ... and Collections__Authorization__IntegrationEmployeeIds__0, __1 ...)
   or appsettings.Production.json / the secret store.  The ids that must be excluded are EXACTLY the union of those two lists.  Nobody and nothing can derive them from data.
   The Genesys integration account is a service identity ONLY when its id is in one of those two lists (see GenesysCollectionsController and ServiceIdentityRestriction).

 WHAT THIS SCRIPT DOES
   1. You paste the ids read from the two settings into @ConfiguredIds (one INSERT per id).  Nothing is ever added from the evidence below.
   2. It VALIDATES each pasted id against the database: is it an employee, active, locked, a Call Center CS Agent who WOULD be dealt tickets if it were not excluded.
   3. It looks for DATABASE EVIDENCE of integration accounts that are NOT in your list, so a forgotten id is caught before a live run:
        TIER_A  creates Genesys tickets (Tickets.CreatedByEmployeeId on a ticket that has a Genesys interaction), has never handled an interaction, and has no Genesys user mapping
        TIER_B  weaker: creates Genesys tickets but also handles interactions or is mapped / acts in Genesys* audit rows / looks like a service account by name only
      Evidence is a prompt to CHECK THE CONFIGURATION - it is never added to the list automatically.
   4. It prints one VERDICT row with the exact, safe values for @ServiceIdentity, @ServiceIdsConfirmed and @NoServiceAccountsExist.

 NOTHING IS CHANGED: only SELECTs and table variables; READ UNCOMMITTED; no customer name / phone / e-mail is selected.  Safe from a db_datareader login at any time.
 It does not touch, bypass or weaken the gate in Backfill_TigerCS_PUBLISHED_ClassificationAndAssignment.sql.
================================================================================
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ExpectedDb sysname = N'TigerCS_Ticketing_Prod';
IF DB_NAME() <> @ExpectedDb
BEGIN
    DECLARE @m nvarchar(400) = N'ABORTED: connected to [' + ISNULL(DB_NAME(), N'?') + N'], this script is for [' + @ExpectedDb + N'] only.';
    THROW 50700, @m, 1;
END;

-- ------------------------------------------------ PASTE FROM CONFIGURATION (the only input) ---------------------------------------------
-- One INSERT per id, copied from the LIVE API pool settings - BOTH lists.  Second column = which setting it came from.
--   INSERT @ConfiguredIds VALUES ('00000000-0000-0000-0000-000000000000', 'Authorization:ServiceIdentity:EmployeeIds');
--   INSERT @ConfiguredIds VALUES ('00000000-0000-0000-0000-000000000000', 'Collections:Authorization:IntegrationEmployeeIds');
DECLARE @ConfiguredIds TABLE (EmployeeId uniqueidentifier NOT NULL, Source varchar(60) NOT NULL);

-- Set to 1 ONLY after you have read BOTH settings on the live API pool and confirmed that BOTH are empty / absent.  Leave 0 otherwise.
DECLARE @BothSettingsVerifiedEmpty bit = 0;
-- ----------------------------------------------------------------------------------------------------------------------------------------

DECLARE @Now datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @CcId int = (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC' AND IsActive = 1);

-- Everyone the backfill WOULD deal tickets to if they were not excluded (same rule as phase 2: active, Call Center member, not locked out, role CS Agent).
DECLARE @Pool TABLE (EmployeeId uniqueidentifier PRIMARY KEY);
INSERT @Pool (EmployeeId)
SELECT e.EmployeeId
FROM dbo.Employees e
JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId
WHERE e.DeactivatedAtUtc IS NULL
  AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments a WHERE a.EmployeeId = e.EmployeeId AND a.DepartmentId = @CcId)
  AND NOT (u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > @Now)
  AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId AND r.Name = N'CS Agent');

-- Evidence per employee (every employee with at least one signal).
DECLARE @Ev TABLE (
    EmployeeId uniqueidentifier PRIMARY KEY, UserName nvarchar(256) NULL, DisplayName nvarchar(256) NULL, IsActive bit NOT NULL, IsLocked bit NOT NULL,
    HasGenesysUserMapping bit NOT NULL, CreatedGenesysTickets int NOT NULL, HandledInteractions int NOT NULL, GenesysAuditRows int NOT NULL,
    NameHint bit NOT NULL, InPool bit NOT NULL, OpenTicketsOwned int NOT NULL, Roles nvarchar(400) NULL, Tier varchar(8) NULL);

INSERT @Ev (EmployeeId, UserName, DisplayName, IsActive, IsLocked, HasGenesysUserMapping, CreatedGenesysTickets, HandledInteractions, GenesysAuditRows, NameHint, InPool, OpenTicketsOwned, Roles)
SELECT e.EmployeeId, u.UserName, e.DisplayName,
       CASE WHEN e.DeactivatedAtUtc IS NULL THEN 1 ELSE 0 END,
       CASE WHEN u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > @Now THEN 1 ELSE 0 END,
       CASE WHEN NULLIF(LTRIM(RTRIM(u.GenesysUserId)), N'') IS NOT NULL THEN 1 ELSE 0 END,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CreatedByEmployeeId = e.EmployeeId
            AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2)),
       (SELECT COUNT(*) FROM dbo.TicketInteractions i WHERE i.HandledByUserId = e.EmployeeId)
         + (SELECT COUNT(*) FROM dbo.TicketInteractionHandlers h WHERE h.HandledByUserId = e.EmployeeId),
       (SELECT COUNT(*) FROM dbo.AuditEntries au WHERE au.ActorEmployeeId = e.EmployeeId AND au.Action LIKE N'Genesys%'),
       CASE WHEN LOWER(ISNULL(u.UserName, N'') + N' ' + ISNULL(u.Email, N'') + N' ' + ISNULL(e.DisplayName, N'')) LIKE N'%genesys%'
              OR LOWER(ISNULL(u.UserName, N'') + N' ' + ISNULL(u.Email, N'') + N' ' + ISNULL(e.DisplayName, N'')) LIKE N'%integration%'
              OR LOWER(ISNULL(u.UserName, N'') + N' ' + ISNULL(u.Email, N'') + N' ' + ISNULL(e.DisplayName, N'')) LIKE N'%service%'
              OR LOWER(ISNULL(u.UserName, N'') + N' ' + ISNULL(u.Email, N'') + N' ' + ISNULL(e.DisplayName, N'')) LIKE N'%svc%'
              OR LOWER(ISNULL(u.UserName, N'') + N' ' + ISNULL(u.Email, N'') + N' ' + ISNULL(e.DisplayName, N'')) LIKE N'%bot%'
              OR LOWER(ISNULL(u.UserName, N'') + N' ' + ISNULL(u.Email, N'') + N' ' + ISNULL(e.DisplayName, N'')) LIKE N'%api%'
            THEN 1 ELSE 0 END,
       CASE WHEN EXISTS (SELECT 1 FROM @Pool p WHERE p.EmployeeId = e.EmployeeId) THEN 1 ELSE 0 END,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = e.EmployeeId AND t.TicketStatus BETWEEN 1 AND 4),
       (SELECT STRING_AGG(r.Name, N', ') FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = e.EmployeeId)
FROM dbo.Employees e
LEFT JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId;

UPDATE @Ev SET Tier =
    CASE WHEN CreatedGenesysTickets > 0 AND HandledInteractions = 0 AND HasGenesysUserMapping = 0 THEN 'TIER_A'
         WHEN CreatedGenesysTickets > 0 OR GenesysAuditRows > 0 OR NameHint = 1 THEN 'TIER_B'
         END;

DECLARE @Cfg TABLE (EmployeeId uniqueidentifier PRIMARY KEY, Sources nvarchar(200) NOT NULL);
INSERT @Cfg (EmployeeId, Sources)
SELECT c.EmployeeId, STRING_AGG(c.Source, N' + ') FROM (SELECT DISTINCT EmployeeId, Source FROM @ConfiguredIds) c GROUP BY c.EmployeeId;

-- ======================================================================================================================
-- RESULT 1  VERDICT and the exact safe configuration  (read this first)
-- ======================================================================================================================
DECLARE @CfgCount int = (SELECT COUNT(*) FROM @Cfg);
DECLARE @NotFound int = (SELECT COUNT(*) FROM @Cfg c WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = c.EmployeeId));
DECLARE @UnlistedA int = (SELECT COUNT(*) FROM @Ev v WHERE v.Tier = 'TIER_A' AND NOT EXISTS (SELECT 1 FROM @Cfg c WHERE c.EmployeeId = v.EmployeeId));
DECLARE @UnlistedB int = (SELECT COUNT(*) FROM @Ev v WHERE v.Tier = 'TIER_B' AND NOT EXISTS (SELECT 1 FROM @Cfg c WHERE c.EmployeeId = v.EmployeeId));
DECLARE @AtRisk   int = (SELECT COUNT(*) FROM @Ev v WHERE v.InPool = 1 AND v.Tier IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @Cfg c WHERE c.EmployeeId = v.EmployeeId));
DECLARE @ConfiguredInPool int = (SELECT COUNT(*) FROM @Cfg c JOIN @Pool p ON p.EmployeeId = c.EmployeeId);

SELECT
    CASE
        WHEN @CfgCount > 0 AND @NotFound > 0
            THEN 'BLOCKER: a pasted id is not an employee (typo?) - see result 3. Fix the id; do not run live.'
        WHEN @CfgCount > 0 AND @UnlistedA > 0
            THEN 'REVIEW BEFORE LIVE: the database shows integration-account evidence (TIER_A) for ids that are NOT in your list - see result 4. Confirm them against the two settings, add any that belong, and re-run this script.'
        WHEN @CfgCount > 0
            THEN 'READY: your pasted list is valid. Paste the lines from result 1b, set @ServiceIdsConfirmed = 1, @NoServiceAccountsExist = 0.'
        WHEN @UnlistedA > 0
            THEN 'BLOCKER: the list is empty but the database shows an account that creates Genesys tickets (TIER_A, result 4). An integration account exists. Paste the ids from the two settings. Do NOT use @NoServiceAccountsExist = 1.'
        WHEN @BothSettingsVerifiedEmpty = 1
            THEN 'READY (empty list): you confirmed both settings are empty and the database shows no TIER_A account. Use @NoServiceAccountsExist = 1, @ServiceIdsConfirmed = 0, nothing to paste.'
        ELSE 'UNDETERMINED: SQL cannot prove that no service account exists. Read the two settings on the live API pool, then either paste their ids into @ConfiguredIds, or - only if both are empty - set @BothSettingsVerifiedEmpty = 1 and re-run.'
    END AS Verdict,
    @CfgCount AS ConfiguredIdCount, @ConfiguredInPool AS ConfiguredIdsThatWouldOtherwiseBeDealtTickets,
    @UnlistedA AS UnlistedTierA, @UnlistedB AS UnlistedTierB, @AtRisk AS UnlistedEvidenceAccountsInThePool,
    CASE WHEN @CfgCount > 0 AND @NotFound = 0 AND @UnlistedA = 0 THEN 1 WHEN @CfgCount = 0 AND @UnlistedA = 0 AND @BothSettingsVerifiedEmpty = 1 THEN 1 ELSE 0 END AS SafeToSetTheGate,
    CASE WHEN @CfgCount > 0 THEN '@ServiceIdsConfirmed = 1' ELSE '@ServiceIdsConfirmed = 0' END AS Setting_ServiceIdsConfirmed,
    CASE WHEN @CfgCount > 0 THEN '@NoServiceAccountsExist = 0' ELSE '@NoServiceAccountsExist = 1  (only if SafeToSetTheGate = 1)' END AS Setting_NoServiceAccountsExist;

-- RESULT 1b  The exact lines for @ServiceIdentity: ONLY the ids you pasted from configuration, nothing derived from the evidence.
SELECT 'INSERT INTO @ServiceIdentity VALUES (''' + LOWER(CONVERT(char(36), c.EmployeeId)) + ''');   ' + CHAR(45) + CHAR(45) + ' ' + c.Sources AS PasteIntoBackfillScript
FROM @Cfg c ORDER BY c.EmployeeId;

-- ======================================================================================================================
-- RESULT 2  the configured ids, validated  (must be excluded - these come from configuration, not from guessing)
-- ======================================================================================================================
SELECT c.EmployeeId, c.Sources AS ConfiguredIn,
       CASE WHEN e.EmployeeId IS NULL THEN 'NOT AN EMPLOYEE - typo? (the live script would abort with Msg 50807)' ELSE 'EMPLOYEE' END AS ExistsCheck,
       u.UserName, e.DisplayName,
       CASE WHEN e.DeactivatedAtUtc IS NULL THEN 'active' ELSE 'DEACTIVATED' END AS State,
       CASE WHEN u.LockoutEnabled = 1 AND u.LockoutEnd IS NOT NULL AND u.LockoutEnd > @Now THEN 'LOCKED OUT' ELSE 'not locked' END AS Lockout,
       CASE WHEN EXISTS (SELECT 1 FROM @Pool p WHERE p.EmployeeId = c.EmployeeId) THEN 'YES - would be dealt tickets if it were not excluded (so the exclusion matters)' ELSE 'no (not an eligible CS Agent of the Call Center anyway)' END AS WouldOtherwiseBeInTheRotation,
       (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId = c.EmployeeId AND t.TicketStatus BETWEEN 1 AND 4) AS ActiveTicketsItOwns,
       (SELECT STRING_AGG(r.Name, N', ') FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = c.EmployeeId) AS Roles
FROM @Cfg c
LEFT JOIN dbo.Employees e ON e.EmployeeId = c.EmployeeId
LEFT JOIN dbo.AspNetUsers u ON u.Id = c.EmployeeId
ORDER BY c.EmployeeId;

-- ======================================================================================================================
-- RESULT 3  pasted ids that do not exist  (empty = good)
-- ======================================================================================================================
SELECT c.EmployeeId AS IdNotFoundInEmployees, c.Sources
FROM @Cfg c WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.EmployeeId = c.EmployeeId);

-- ======================================================================================================================
-- RESULT 4  evidence of integration accounts that are NOT in your list  (check each against the two settings; empty = nothing found)
--   InPool = YES means: if this really is a service account and stays unlisted, the live run could deal it tickets.
-- ======================================================================================================================
SELECT v.Tier, v.EmployeeId, v.UserName, v.DisplayName, v.Roles,
       CASE WHEN v.IsActive = 1 THEN 'active' ELSE 'DEACTIVATED' END AS State,
       CASE WHEN v.InPool = 1 THEN 'YES' ELSE 'no' END AS InPool_WouldBeDealtTickets,
       v.CreatedGenesysTickets, v.HandledInteractions, v.GenesysAuditRows,
       CASE WHEN v.HasGenesysUserMapping = 1 THEN 'mapped to a Genesys agent' ELSE 'no Genesys user mapping' END AS GenesysMapping,
       CASE WHEN v.NameHint = 1 THEN 'name looks like a service account' ELSE '' END AS NameHint,
       v.OpenTicketsOwned
FROM @Ev v
WHERE v.Tier IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @Cfg c WHERE c.EmployeeId = v.EmployeeId)
ORDER BY v.Tier, v.InPool DESC, v.CreatedGenesysTickets DESC, v.EmployeeId;

-- ======================================================================================================================
-- RESULT 5  who creates Genesys tickets  (the integration account(s) in the data - compare with the two settings)
-- ======================================================================================================================
SELECT TOP (50) t.CreatedByEmployeeId AS EmployeeId, u.UserName, COUNT(*) AS GenesysTicketsCreated,
       MIN(t.CreatedAtUtc) AS FirstCreatedUtc, MAX(t.CreatedAtUtc) AS LastCreatedUtc,
       CASE WHEN EXISTS (SELECT 1 FROM @Cfg c WHERE c.EmployeeId = t.CreatedByEmployeeId) THEN 'listed in configuration' ELSE 'NOT in your pasted list' END AS ConfigStatus
FROM dbo.Tickets t
LEFT JOIN dbo.AspNetUsers u ON u.Id = t.CreatedByEmployeeId
WHERE t.CreatedByEmployeeId IS NOT NULL
  AND EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId = t.TicketId AND i.Source = 2)
GROUP BY t.CreatedByEmployeeId, u.UserName
ORDER BY COUNT(*) DESC;

-- ======================================================================================================================
-- RESULT 6  every Call Center CS Agent the live run could deal tickets to, and whether it is excluded  (the rotation as the backfill will see it)
-- ======================================================================================================================
SELECT ROW_NUMBER() OVER (ORDER BY CONVERT(char(36), p.EmployeeId) COLLATE Latin1_General_BIN2) AS RotationOrder,
       p.EmployeeId, u.UserName, e.DisplayName,
       CASE WHEN EXISTS (SELECT 1 FROM @Cfg c WHERE c.EmployeeId = p.EmployeeId) THEN 'EXCLUDED (listed as service account)' ELSE 'in rotation' END AS Status,
       CASE WHEN NULLIF(LTRIM(RTRIM(u.GenesysUserId)), N'') IS NOT NULL THEN 'mapped' ELSE 'no Genesys mapping' END AS GenesysMapping,
       v.Tier AS ServiceEvidence
FROM @Pool p
JOIN dbo.Employees e ON e.EmployeeId = p.EmployeeId
JOIN dbo.AspNetUsers u ON u.Id = p.EmployeeId
LEFT JOIN @Ev v ON v.EmployeeId = p.EmployeeId
ORDER BY RotationOrder;
