-- ############################################################################################
-- ## Create_Handover_Users.sql
-- ## Creates two Handover users the same way User Management does (AdminUserAppService.CreateAsync):
-- ##   AspNetUsers row + Employees row + AspNetUserRoles row + primary UserDepartmentAssignments row
-- ##   + one AuditEntries row ("AdminCreateUser") per user.
-- ##
-- ##   Shahd Al-Khaldi  C.Alkhalidi@tigergroup.ae   Handover (HO, primary)   Department Employee   Active
-- ##   Susi Awad        s.aouad@tigergroup.ae       Handover (HO, primary)   Department Employee   Active
-- ##
-- ## NOT executed by the author against any database (UAT or Production). Review, run in PREVIEW on
-- ## UAT first, then commit deliberately. Production is run only after approval and a backup.
-- ############################################################################################
--
-- WHAT IT DOES NOT DO: no schema change, no change to any existing user, role, permission, department,
-- department membership or rotation. An account that already exists is only REPORTED, never modified.
-- It creates no role and no department; both must already exist (Department Employee, HO).
--
-- PASSWORDS (important)
--   * The script contains NO plaintext password and NO real hash. T-SQL has no PBKDF2, so the password
--     hash is generated OUTSIDE SQL with the real Identity algorithm and pasted in below as
--     @PasswordHash_Shahd / @PasswordHash_Susi (an "AQAAAA..." string, ASP.NET Identity V3 format).
--   * The script checks the hash STRUCTURE before it writes anything (V3 marker, HMAC-SHA256/512,
--     >= 10000 iterations, salt >= 16 bytes, subkey >= 32 bytes, valid base64). A plaintext password,
--     a placeholder or a truncated string is rejected. It cannot check the PASSWORD POLICY (Identity
--     requires >= 8 chars, a digit, lower, upper, symbol, >= 4 unique chars): choose compliant passwords.
--   * Generate each hash on a trusted machine (pick a password, hash it, hand the password over securely,
--     tell the user to change it). Do not commit a filled-in copy of this file to source control.
--     Option A - C# (the exact hasher the application uses), e.g. in a throw-away console project that
--                references Microsoft.AspNetCore.Identity:
--         var h = new Microsoft.AspNetCore.Identity.PasswordHasher<object>();
--         Console.WriteLine(h.HashPassword(new object(), password));
--     Option B - Python 3 (same format as ASP.NET Core 7+ Identity: PBKDF2-HMAC-SHA512, 100000, 16B salt, 32B key):
--         import os,hashlib,base64,struct,getpass
--         pw=getpass.getpass().encode(); salt=os.urandom(16)
--         key=hashlib.pbkdf2_hmac('sha512',pw,salt,100000,32)
--         print(base64.b64encode(b'\x01'+struct.pack('>III',2,100000,16)+salt+key).decode())
--     Identity reads the algorithm/iterations from the hash itself, so either option verifies at sign-in.
--   * After sign-in works, a password can be reset any time in User Management (existing mechanism).
--
-- HOW TO RUN (SSMS, connected to the target server, correct database selected):
--   1. Leave @PreviewMode = 1. Run. Everything is applied inside a transaction, verified, then ROLLED BACK.
--      Hashes may stay NULL in preview. Read the Messages tab and the result grids.
--   2. To commit: set @PreviewMode = 0, set @ExpectedDatabase to the exact database name you intend
--      (the script refuses to commit otherwise), paste both hashes, optionally set @ActorEmail to the
--      active System Administrator who authorises this (named in the audit entries), and run again.
--   3. Re-running after success changes nothing (accounts are matched by user name / email).
-- Run as ONE batch (there is intentionally no GO in this file).
-- Requires SQL Server 2016+ (same as the application).

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- =============================================================================================
-- SETTINGS
-- =============================================================================================
DECLARE @PreviewMode       bit            = 1;      -- 1 = apply, verify, ROLL BACK (default). 0 = apply and COMMIT.
DECLARE @ExpectedDatabase  sysname        = NULL;   -- REQUIRED to commit: must equal DB_NAME(). Also enforced in preview when set.
DECLARE @ActorEmail        nvarchar(256)  = NULL;   -- Optional: active System Administrator named in the audit entries. NULL = no actor.
DECLARE @PasswordHash_Shahd nvarchar(4000) = NULL;  -- ASP.NET Identity V3 hash ("AQAAAA..."), NOT a password. Required only to commit.
DECLARE @PasswordHash_Susi  nvarchar(4000) = NULL;  -- ASP.NET Identity V3 hash ("AQAAAA..."), NOT a password. Required only to commit.

-- Fixed by the request (do not change).
DECLARE @DepartmentCode nvarchar(10)  = N'HO';
DECLARE @RoleName       nvarchar(256) = N'Department Employee';

-- =============================================================================================
-- 1. Target guard (nothing is read or written before this passes)
-- =============================================================================================
DECLARE @msg nvarchar(2000);
SET @msg = N'Server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + ISNULL(DB_NAME(), N'?')
         + N'], PreviewMode=' + CAST(@PreviewMode AS nvarchar(1));
PRINT @msg;

IF @PreviewMode = 0 AND @ExpectedDatabase IS NULL
    THROW 50200, 'ABORTED: to commit, set @ExpectedDatabase to the exact name of the database you intend to change. Nothing was changed.', 1;

IF @ExpectedDatabase IS NOT NULL AND DB_NAME() <> @ExpectedDatabase
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N'] but @ExpectedDatabase is [' + @ExpectedDatabase + N']. Nothing was changed.';
    THROW 50201, @msg, 1;
END;

-- =============================================================================================
-- 2. Schema pre-check (read-only): every table/column this script touches must exist
-- =============================================================================================
DECLARE @missing nvarchar(max) = NULL;
SELECT @missing = ISNULL(@missing + N', ', N'') + v.t + N'.' + v.c
FROM (VALUES
    (N'dbo.AspNetUsers', N'Id'), (N'dbo.AspNetUsers', N'UserName'), (N'dbo.AspNetUsers', N'NormalizedUserName'),
    (N'dbo.AspNetUsers', N'Email'), (N'dbo.AspNetUsers', N'NormalizedEmail'), (N'dbo.AspNetUsers', N'EmailConfirmed'),
    (N'dbo.AspNetUsers', N'PasswordHash'), (N'dbo.AspNetUsers', N'SecurityStamp'), (N'dbo.AspNetUsers', N'ConcurrencyStamp'),
    (N'dbo.AspNetUsers', N'PhoneNumberConfirmed'), (N'dbo.AspNetUsers', N'TwoFactorEnabled'), (N'dbo.AspNetUsers', N'LockoutEnabled'),
    (N'dbo.AspNetUsers', N'AccessFailedCount'),
    (N'dbo.AspNetRoles', N'Id'), (N'dbo.AspNetRoles', N'Name'),
    (N'dbo.AspNetUserRoles', N'UserId'), (N'dbo.AspNetUserRoles', N'RoleId'),
    (N'dbo.Employees', N'EmployeeId'), (N'dbo.Employees', N'DisplayName'), (N'dbo.Employees', N'IsGeynessStaff'),
    (N'dbo.Employees', N'DeactivatedAtUtc'), (N'dbo.Employees', N'CreatedAtUtc'),
    (N'dbo.Departments', N'DepartmentId'), (N'dbo.Departments', N'Code'), (N'dbo.Departments', N'Name'), (N'dbo.Departments', N'IsActive'),
    (N'dbo.UserDepartmentAssignments', N'UserDepartmentAssignmentId'), (N'dbo.UserDepartmentAssignments', N'EmployeeId'),
    (N'dbo.UserDepartmentAssignments', N'DepartmentId'), (N'dbo.UserDepartmentAssignments', N'IsPrimary'),
    (N'dbo.UserDepartmentAssignments', N'AssignedAtUtc'), (N'dbo.UserDepartmentAssignments', N'AssignedByEmployeeId'),
    (N'dbo.AuditEntries', N'ActorEmployeeId'), (N'dbo.AuditEntries', N'Action'), (N'dbo.AuditEntries', N'EntityType'),
    (N'dbo.AuditEntries', N'EntityId'), (N'dbo.AuditEntries', N'BeforeValue'), (N'dbo.AuditEntries', N'AfterValue'),
    (N'dbo.AuditEntries', N'CorrelationId'), (N'dbo.AuditEntries', N'OccurredAtUtc')
) AS v(t, c)
WHERE COL_LENGTH(v.t, v.c) IS NULL;

IF @missing IS NOT NULL
BEGIN
    SET @msg = N'ABORTED: schema does not match what this script expects; missing: ' + @missing + N'. Wrong database, or migrations not applied. Nothing was changed.';
    THROW 50210, @msg, 1;
END;

-- =============================================================================================
-- 3. Resolve the existing department, role and (optional) actor. Nothing is created.
-- =============================================================================================
DECLARE @DepartmentId int, @DepartmentName nvarchar(100), @DepartmentActive bit, @DeptCount int;
SELECT @DeptCount = COUNT(*) FROM dbo.Departments WHERE Code = @DepartmentCode;
IF @DeptCount <> 1
BEGIN
    SET @msg = N'ABORTED: expected exactly one department with code [' + @DepartmentCode + N'], found ' + CAST(@DeptCount AS nvarchar(10)) + N'. No department is created. Nothing was changed.';
    THROW 50220, @msg, 1;
END;
SELECT @DepartmentId = DepartmentId, @DepartmentName = Name, @DepartmentActive = IsActive FROM dbo.Departments WHERE Code = @DepartmentCode;
IF @DepartmentActive = 0
    THROW 50221, 'ABORTED: the Handover department is inactive and cannot receive new members. Nothing was changed.', 1;

DECLARE @RoleId uniqueidentifier, @RoleCount int;
SELECT @RoleCount = COUNT(*) FROM dbo.AspNetRoles WHERE Name = @RoleName;
IF @RoleCount <> 1
BEGIN
    SET @msg = N'ABORTED: expected exactly one role named [' + @RoleName + N'], found ' + CAST(@RoleCount AS nvarchar(10)) + N'. No role is created. Nothing was changed.';
    THROW 50222, @msg, 1;
END;
SELECT @RoleId = Id FROM dbo.AspNetRoles WHERE Name = @RoleName;

DECLARE @ActorEmployeeId uniqueidentifier = NULL;
IF @ActorEmail IS NOT NULL
BEGIN
    SELECT @ActorEmployeeId = u.Id
    FROM dbo.AspNetUsers u
    JOIN dbo.Employees e ON e.EmployeeId = u.Id AND e.DeactivatedAtUtc IS NULL
    JOIN dbo.AspNetUserRoles ur ON ur.UserId = u.Id
    JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId AND r.Name = N'System Administrator'
    WHERE u.NormalizedUserName = UPPER(LTRIM(RTRIM(@ActorEmail))) OR u.NormalizedEmail = UPPER(LTRIM(RTRIM(@ActorEmail)));
    IF @ActorEmployeeId IS NULL
        THROW 50223, 'ABORTED: @ActorEmail is not an active System Administrator. Nothing was changed.', 1;
END;

-- =============================================================================================
-- 4. The roster. User name = lower-cased email (as User Management stores it); Email as supplied;
--    normalised keys = UPPER (Identity's invariant upper-case normaliser).
-- =============================================================================================
DECLARE @Users TABLE
(
    Seq          int            NOT NULL PRIMARY KEY,
    FullName     nvarchar(200)  NOT NULL,
    Email        nvarchar(256)  NOT NULL,
    UserName     nvarchar(256)  NOT NULL,
    NormUser     nvarchar(256)  NOT NULL,
    NormEmail    nvarchar(256)  NOT NULL,
    PwdHash      nvarchar(4000) NULL,
    MatchCount   int            NULL,
    ExistingId   uniqueidentifier NULL,
    NewEmployeeId        uniqueidentifier NULL,
    Status       nvarchar(20)   NULL,      -- NEW | EXISTS_OK | EXISTS_REVIEW | AMBIGUOUS
    Detail       nvarchar(400)  NULL
);

INSERT INTO @Users (Seq, FullName, Email, UserName, NormUser, NormEmail, PwdHash)
SELECT s.Seq, s.FullName, LTRIM(RTRIM(s.Email)), LOWER(LTRIM(RTRIM(s.Email))), UPPER(LOWER(LTRIM(RTRIM(s.Email)))), UPPER(LTRIM(RTRIM(s.Email))), s.PwdHash
FROM (VALUES
    (1, N'Shahd Al-Khaldi', N'C.Alkhalidi@tigergroup.ae', @PasswordHash_Shahd),
    (2, N'Susi Awad',       N's.aouad@tigergroup.ae',     @PasswordHash_Susi)
) AS s(Seq, FullName, Email, PwdHash);

IF (SELECT COUNT(DISTINCT NormUser) FROM @Users) <> (SELECT COUNT(*) FROM @Users)
    THROW 50230, 'ABORTED: the roster lists the same user name twice. Nothing was changed.', 1;

-- =============================================================================================
-- 5. Password-hash structure check (any supplied hash, in every mode). Never prints a hash.
--    ASP.NET Identity V3: 0x01 | prf(4,BE) | iterations(4,BE) | saltLen(4,BE) | salt | subkey
-- =============================================================================================
DECLARE @i int = 1, @n int = (SELECT COUNT(*) FROM @Users), @h nvarchar(4000), @bin varbinary(max), @problem nvarchar(200);
DECLARE @iter int, @saltLen int, @prf varbinary(4);

WHILE @i <= @n
BEGIN
    SELECT @h = PwdHash FROM @Users WHERE Seq = @i;
    SET @problem = NULL;

    IF @h IS NOT NULL
    BEGIN
        SET @bin = NULL;
        IF @h <> LTRIM(RTRIM(@h)) OR @h COLLATE Latin1_General_BIN LIKE N'%[^A-Za-z0-9+/=]%'
            SET @problem = N'contains characters that are not base64 (looks like a password or has spaces)';
        ELSE
        BEGIN
            BEGIN TRY
                SET @bin = CAST(N'' AS xml).value('xs:base64Binary(sql:variable("@h"))', 'varbinary(max)');
            END TRY
            BEGIN CATCH
                SET @bin = NULL;
            END CATCH;

            IF @bin IS NULL OR DATALENGTH(@bin) < 45
                SET @problem = N'is not a valid, complete base64 Identity V3 hash';
            ELSE
            BEGIN
                SET @prf     = CAST(SUBSTRING(@bin, 2, 4) AS binary(4));
                SET @iter    = CAST(SUBSTRING(@bin, 6, 4) AS int);
                SET @saltLen = CAST(SUBSTRING(@bin, 10, 4) AS int);
                IF SUBSTRING(@bin, 1, 1) <> 0x01
                    SET @problem = N'is not Identity V3 format (first byte must be 0x01; V2 hashes start 0x00)';
                ELSE IF @prf NOT IN (0x00000001, 0x00000002)
                    SET @problem = N'uses an unsupported PRF (must be HMAC-SHA256 or HMAC-SHA512)';
                ELSE IF @iter < 10000
                    SET @problem = N'has too few PBKDF2 iterations (< 10000)';
                ELSE IF @saltLen < 16 OR DATALENGTH(@bin) - 13 - @saltLen < 32
                    SET @problem = N'has a salt shorter than 16 bytes or a derived key shorter than 32 bytes';
            END;
        END;
    END;

    IF @problem IS NOT NULL
    BEGIN
        SET @msg = N'ABORTED: the password hash for roster entry ' + CAST(@i AS nvarchar(10)) + N' ' + @problem + N'. Nothing was changed.';
        THROW 50240, @msg, 1;
    END;

    SET @i += 1;
END;

-- =============================================================================================
-- 6. The transaction: classify, create what is missing, verify, then COMMIT or (preview) ROLLBACK
-- =============================================================================================
BEGIN TRY
    BEGIN TRANSACTION;

    -- 6a. Duplicate check (locks the rows it reads so two concurrent runs cannot both create).
    UPDATE u
    SET MatchCount = (SELECT COUNT(*) FROM dbo.AspNetUsers a WITH (UPDLOCK, HOLDLOCK)
                      WHERE a.NormalizedUserName = u.NormUser OR a.NormalizedEmail = u.NormEmail
                         OR a.UserName = u.UserName OR a.Email = u.Email),
        ExistingId = (SELECT TOP (1) a.Id FROM dbo.AspNetUsers a WITH (UPDLOCK, HOLDLOCK)
                      WHERE a.NormalizedUserName = u.NormUser OR a.NormalizedEmail = u.NormEmail
                         OR a.UserName = u.UserName OR a.Email = u.Email
                      ORDER BY a.Id)
    FROM @Users u;

    UPDATE @Users SET Status = N'NEW', Detail = N'No account with this user name or email; will be created.' WHERE MatchCount = 0;
    UPDATE @Users SET Status = N'AMBIGUOUS', Detail = N'More than one existing account matches this user name/email; needs manual review. Not modified.' WHERE MatchCount > 1;

    -- An existing account is only classified, never changed.
    UPDATE u
    SET Status = CASE WHEN e.EmployeeId IS NOT NULL AND e.DeactivatedAtUtc IS NULL
                           AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments d WHERE d.EmployeeId = u.ExistingId AND d.DepartmentId = @DepartmentId AND d.IsPrimary = 1)
                           AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles r WHERE r.UserId = u.ExistingId AND r.RoleId = @RoleId)
                      THEN N'EXISTS_OK' ELSE N'EXISTS_REVIEW' END,
        Detail = CASE WHEN e.EmployeeId IS NULL THEN N'Identity account exists without an Employee profile; needs manual review. Not modified.'
                      WHEN e.DeactivatedAtUtc IS NOT NULL THEN N'Account exists but is deactivated; not reactivated. Not modified.'
                      WHEN EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments d WHERE d.EmployeeId = u.ExistingId AND d.DepartmentId = @DepartmentId AND d.IsPrimary = 1)
                           AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles r WHERE r.UserId = u.ExistingId AND r.RoleId = @RoleId)
                      THEN N'Already exists as active ' + @RoleName + N' in ' + @DepartmentName + N'; nothing written.'
                      ELSE N'Account exists with a different role or department; existing assignments left untouched. Not modified.' END
    FROM @Users u
    LEFT JOIN dbo.Employees e ON e.EmployeeId = u.ExistingId
    WHERE u.MatchCount = 1;

    -- A real (committed) run cannot create a login without a valid hash.
    IF @PreviewMode = 0 AND EXISTS (SELECT 1 FROM @Users WHERE Status = N'NEW' AND PwdHash IS NULL)
        THROW 50241, 'ABORTED: a new user has no password hash. Paste the Identity V3 hash (see header) or run in preview. Nothing was changed.', 1;

    -- 6b. Create missing users (same rows User Management writes).
    UPDATE @Users SET NewEmployeeId = NEWID() WHERE Status = N'NEW';

    INSERT INTO dbo.AspNetUsers
        (Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp, ConcurrencyStamp,
         PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount, GenesysUserId, GenesysEmail)
    SELECT NewEmployeeId, UserName, NormUser, Email, NormEmail, 0, PwdHash,
           REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N''),     -- SecurityStamp (any unique string; Identity rotates it on password reset)
           LOWER(CONVERT(nvarchar(36), NEWID())),                  -- ConcurrencyStamp
           NULL, 0, 0, NULL, 1, 0, NULL, NULL                      -- LockoutEnabled = 1 (Lockout.AllowedForNewUsers), not a Genesys agent
    FROM @Users WHERE Status = N'NEW';

    INSERT INTO dbo.Employees (EmployeeId, DisplayName, IsGeynessStaff, DeactivatedAtUtc, CreatedAtUtc)
    SELECT NewEmployeeId, LTRIM(RTRIM(FullName)), 0, NULL, SYSUTCDATETIME()      -- DeactivatedAtUtc NULL = Active
    FROM @Users WHERE Status = N'NEW';

    INSERT INTO dbo.AspNetUserRoles (UserId, RoleId)
    SELECT NewEmployeeId, @RoleId FROM @Users WHERE Status = N'NEW';

    INSERT INTO dbo.UserDepartmentAssignments (EmployeeId, DepartmentId, IsPrimary, AssignedAtUtc, AssignedByEmployeeId)
    SELECT NewEmployeeId, @DepartmentId, 1, SYSUTCDATETIME(), @ActorEmployeeId
    FROM @Users WHERE Status = N'NEW';

    INSERT INTO dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
    SELECT @ActorEmployeeId, N'AdminCreateUser', N'Employee', CONVERT(nvarchar(100), NewEmployeeId), NULL,
           N'UserName=' + UserName + N';DisplayName=' + LTRIM(RTRIM(FullName)) + N';Roles=' + @RoleName
           + N';PrimaryDepartmentId=' + CAST(@DepartmentId AS nvarchar(10)) + N';Source=Create_Handover_Users.sql',
           NEWID(), SYSUTCDATETIME()
    FROM @Users WHERE Status = N'NEW';

    -- 6c. Verification (inside the transaction, so a preview shows exactly what would be committed).
    --     Never selects a password hash; only its format.
    SELECT N'1. Plan / outcome' AS [Check], Seq, FullName, Email, Status, Detail, COALESCE(NewEmployeeId, ExistingId) AS EmployeeId FROM @Users ORDER BY Seq;

    SELECT N'2. Account' AS [Check], u.UserName, u.Email, u.EmailConfirmed, u.LockoutEnabled, u.AccessFailedCount,
           CASE WHEN u.PasswordHash LIKE N'AQAAAA%' THEN N'Identity V3 hash present' WHEN u.PasswordHash IS NULL THEN N'NO HASH (preview only)' ELSE N'other format' END AS PasswordHashFormat,
           CASE WHEN u.SecurityStamp IS NOT NULL THEN 1 ELSE 0 END AS HasSecurityStamp
    FROM dbo.AspNetUsers u JOIN @Users x ON x.NormUser = u.NormalizedUserName ORDER BY x.Seq;

    SELECT N'3. Employee (Active = DeactivatedAtUtc is NULL)' AS [Check], e.EmployeeId, e.DisplayName, e.IsGeynessStaff, e.DeactivatedAtUtc,
           CASE WHEN e.DeactivatedAtUtc IS NULL THEN N'Active' ELSE N'Inactive' END AS Status
    FROM dbo.Employees e JOIN dbo.AspNetUsers u ON u.Id = e.EmployeeId JOIN @Users x ON x.NormUser = u.NormalizedUserName ORDER BY x.Seq;

    SELECT N'4. Roles (must be exactly Department Employee)' AS [Check], u.UserName, r.Name AS RoleName
    FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetUsers u ON u.Id = ur.UserId JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId
    JOIN @Users x ON x.NormUser = u.NormalizedUserName ORDER BY x.Seq, r.Name;

    SELECT N'5. Departments (must be exactly one: Handover, primary)' AS [Check], u.UserName, d.Code, d.Name, a.IsPrimary, a.AssignedAtUtc
    FROM dbo.UserDepartmentAssignments a JOIN dbo.Departments d ON d.DepartmentId = a.DepartmentId
    JOIN dbo.AspNetUsers u ON u.Id = a.EmployeeId JOIN @Users x ON x.NormUser = u.NormalizedUserName ORDER BY x.Seq;

    SELECT N'6. PASS/FAIL per user' AS [Check], x.Email,
           CASE WHEN (SELECT COUNT(*) FROM dbo.AspNetUsers a WHERE a.NormalizedUserName = x.NormUser) = 1
                 AND EXISTS (SELECT 1 FROM dbo.Employees e JOIN dbo.AspNetUsers a ON a.Id = e.EmployeeId WHERE a.NormalizedUserName = x.NormUser AND e.DeactivatedAtUtc IS NULL)
                 AND (SELECT COUNT(*) FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetUsers a ON a.Id = ur.UserId WHERE a.NormalizedUserName = x.NormUser) = 1
                 AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetUsers a ON a.Id = ur.UserId WHERE a.NormalizedUserName = x.NormUser AND ur.RoleId = @RoleId)
                 AND (SELECT COUNT(*) FROM dbo.UserDepartmentAssignments d JOIN dbo.AspNetUsers a ON a.Id = d.EmployeeId WHERE a.NormalizedUserName = x.NormUser) = 1
                 AND EXISTS (SELECT 1 FROM dbo.UserDepartmentAssignments d JOIN dbo.AspNetUsers a ON a.Id = d.EmployeeId WHERE a.NormalizedUserName = x.NormUser AND d.DepartmentId = @DepartmentId AND d.IsPrimary = 1)
                THEN N'PASS' ELSE N'FAIL / REVIEW' END AS Result
    FROM @Users x ORDER BY x.Seq;

    -- 6d. Decide.
    IF @PreviewMode = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'PREVIEW - everything above was applied and then ROLLED BACK. Nothing is stored. Set @PreviewMode = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT N'COMMITTED. Users created: ' + CAST((SELECT COUNT(*) FROM @Users WHERE Status = N'NEW') AS nvarchar(10))
            + N'; already existing / not modified: ' + CAST((SELECT COUNT(*) FROM @Users WHERE Status <> N'NEW') AS nvarchar(10)) + N'.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    SET @msg = N'FAILED and rolled back - nothing was changed. Error ' + CAST(ERROR_NUMBER() AS nvarchar(20)) + N' (line ' + CAST(ERROR_LINE() AS nvarchar(20)) + N'): ' + ERROR_MESSAGE();
    PRINT @msg;
    THROW;
END CATCH;

-- =============================================================================================
-- 7. Committed-state check (read-only, runs after the transaction). In preview this must show 0 rows
--    for any user that did not already exist - proving the rollback left nothing behind.
-- =============================================================================================
SELECT N'7. Stored state after run' AS [Check], u.UserName, u.Email, e.DisplayName,
       CASE WHEN e.DeactivatedAtUtc IS NULL THEN N'Active' ELSE N'Inactive' END AS Status,
       STUFF((SELECT N', ' + r.Name FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE ur.UserId = u.Id ORDER BY r.Name FOR XML PATH(N'')), 1, 2, N'') AS Roles,
       STUFF((SELECT N', ' + d.Code + CASE WHEN a.IsPrimary = 1 THEN N' (primary)' ELSE N'' END FROM dbo.UserDepartmentAssignments a JOIN dbo.Departments d ON d.DepartmentId = a.DepartmentId WHERE a.EmployeeId = u.Id ORDER BY d.Code FOR XML PATH(N'')), 1, 2, N'') AS Departments
FROM dbo.AspNetUsers u
LEFT JOIN dbo.Employees e ON e.EmployeeId = u.Id
WHERE u.NormalizedUserName IN (N'C.ALKHALIDI@TIGERGROUP.AE', N'S.AOUAD@TIGERGROUP.AE')
ORDER BY u.UserName;
