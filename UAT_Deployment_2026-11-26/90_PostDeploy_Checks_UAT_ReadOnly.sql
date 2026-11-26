-- ############################################################################################
-- ## 90_PostDeploy_Checks_UAT_ReadOnly.sql   TigerCS UAT - READ-ONLY post-deployment checks
-- ## Database : TigerCsTicketing (any other database aborts). Writes NOTHING (table variable only).
-- ## Run AFTER steps 01 - 06. Every row must be PASS. Then run 99_Verify_TigerCS_UAT_ReadOnly.sql for the full-schema comparison.
-- ############################################################################################
SET NOCOUNT ON;
SET LOCK_TIMEOUT 10000;

IF DB_NAME() IS NULL OR DB_NAME() <> N'TigerCsTicketing'
    THROW 50700, N'ABORTED: this check is for database TigerCsTicketing (UAT). Connect to it and run again.', 1;

DECLARE @R TABLE (Id int IDENTITY(1,1), CheckName nvarchar(200), Expected nvarchar(400), Actual nvarchar(400), Status varchar(8));
DECLARE @Chain TABLE (MigrationId nvarchar(150) PRIMARY KEY);
INSERT @Chain VALUES (N'20261118120000_AddCustomerProfileOverrides'), (N'20261119081103_AddCustomerProfileAuditAndProperties'),
                     (N'20261119120249_AddAssignmentHistoryPreviousDepartment'), (N'20261120074059_AddInteractionEndReceivedAt'), (N'20261124071552_AddEmployeeReviews');

-- 1 migration history
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Recorded with ProductVersion 10.0.11: ' + c.MigrationId, N'recorded once, 10.0.11',
       CAST((SELECT COUNT(*) FROM dbo.__EFMigrationsHistory h WHERE h.MigrationId = c.MigrationId) AS nvarchar(5)) + N' row(s), version ' + ISNULL((SELECT MAX(h.ProductVersion) FROM dbo.__EFMigrationsHistory h WHERE h.MigrationId = c.MigrationId), N'-'),
       CASE WHEN (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory h WHERE h.MigrationId = c.MigrationId AND h.ProductVersion = N'10.0.11') = 1 THEN 'PASS' ELSE 'FAIL' END
FROM @Chain c;
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Total migrations in history', N'60', CAST(COUNT(*) AS nvarchar(10)), CASE WHEN COUNT(*) = 60 THEN 'PASS' ELSE 'FAIL' END FROM dbo.__EFMigrationsHistory;

-- 2 schema of the five migrations
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Columns of dbo.' + v.t, CAST(v.n AS nvarchar(10)), CAST(ISNULL((SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.' + v.t)), 0) AS nvarchar(10)),
       CASE WHEN ISNULL((SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.' + v.t)), 0) = v.n THEN 'PASS' ELSE 'FAIL' END
FROM (VALUES (N'CustomerProfileOverrides', 25), (N'CustomerProfileChanges', 14), (N'CustomerPropertyAssociations', 11), (N'EmployeeReviewDevices', 8), (N'EmployeeReviews', 7)) v(t, n);
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Column ' + v.t + N'.' + v.c, v.ty + N' NULL', ISNULL(ty.name + CASE WHEN c.is_nullable = 1 THEN N' NULL' ELSE N' NOT NULL' END, N'missing'),
       CASE WHEN ty.name = v.ty AND c.is_nullable = 1 THEN 'PASS' ELSE 'FAIL' END
FROM (VALUES (N'TicketAssignmentHistory', N'PreviousDepartmentId', N'int'), (N'TicketInteractions', N'EndReceivedAtUtc', N'datetime2')) v(t, c, ty)
LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + v.t) AND c.name = v.c
LEFT JOIN sys.types ty ON ty.user_type_id = c.user_type_id;
INSERT @R (CheckName, Expected, Actual, Status)
SELECT N'Index ' + v.i, N'exists, unique=' + CAST(v.u AS nvarchar(1)), CASE WHEN x.name IS NULL THEN N'missing' ELSE N'exists, unique=' + CAST(x.is_unique AS nvarchar(1)) END,
       CASE WHEN x.name IS NOT NULL AND x.is_unique = v.u THEN 'PASS' ELSE 'FAIL' END
FROM (VALUES (N'IX_CustomerProfileOverrides_CustomerKey', 1), (N'IX_CustomerProfileOverrides_CustomerProfileId', 1), (N'IX_CustomerProfileChanges_ChangeKey', 1),
             (N'IX_CustomerProfileChanges_CorrelationId', 0), (N'IX_CustomerProfileChanges_CustomerKey', 0), (N'IX_CustomerProfileChanges_CustomerProfileId_ModifiedAtUtc', 0),
             (N'IX_CustomerPropertyAssociations_CustomerKey', 0), (N'IX_CustomerPropertyAssociations_CustomerProfileId_ProjectName_UnitNumber', 1),
             (N'IX_EmployeeReviewDevices_EmployeeId', 0), (N'UX_EmployeeReviewDevices_TokenHash', 1), (N'IX_EmployeeReviews_CreatedAtUtc', 0),
             (N'IX_EmployeeReviews_DeviceId', 0), (N'IX_EmployeeReviews_EmployeeId_CreatedAtUtc', 0), (N'UX_EmployeeReviews_SubmissionId', 1)) v(i, u)
LEFT JOIN sys.indexes x ON x.name = v.i AND x.object_id IN (OBJECT_ID(N'dbo.CustomerProfileOverrides'), OBJECT_ID(N'dbo.CustomerProfileChanges'), OBJECT_ID(N'dbo.CustomerPropertyAssociations'),
                                                           OBJECT_ID(N'dbo.EmployeeReviewDevices'), OBJECT_ID(N'dbo.EmployeeReviews'));
INSERT @R (CheckName, Expected, Actual, Status)
VALUES (N'Trigger TR_CustomerProfileChanges_AppendOnly', N'INSTEAD OF, enabled',
        ISNULL((SELECT CASE WHEN is_instead_of_trigger = 1 THEN N'INSTEAD OF' ELSE N'AFTER' END + CASE WHEN is_disabled = 1 THEN N', DISABLED' ELSE N', enabled' END FROM sys.triggers WHERE name = N'TR_CustomerProfileChanges_AppendOnly'), N'missing'),
        CASE WHEN EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'TR_CustomerProfileChanges_AppendOnly' AND is_instead_of_trigger = 1 AND is_disabled = 0) THEN 'PASS' ELSE 'FAIL' END),
       (N'CK_EmployeeReviews_Rating', N'exists', CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_EmployeeReviews_Rating') THEN N'exists' ELSE N'missing' END,
        CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_EmployeeReviews_Rating') THEN 'PASS' ELSE 'FAIL' END),
       (N'Foreign keys EmployeeReviewDevices / EmployeeReviews', N'1 / 2',
        CAST((SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviewDevices')) AS nvarchar(5)) + N' / ' + CAST((SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviews')) AS nvarchar(5)),
        CASE WHEN (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviewDevices')) = 1 AND (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.EmployeeReviews')) = 2 THEN 'PASS' ELSE 'FAIL' END);

-- 3 data integrity of the one data-touching step (CustomerProfileId back-fill) - dynamic SQL so the batch compiles on a database that is not deployed yet
DECLARE @zero bigint = -1, @dup bigint = -1;
IF COL_LENGTH(N'dbo.CustomerProfileOverrides', N'CustomerProfileId') IS NOT NULL
BEGIN
    EXEC sp_executesql N'SELECT @z = COUNT_BIG(*) FROM dbo.CustomerProfileOverrides WHERE CustomerProfileId = ''00000000-0000-0000-0000-000000000000'';', N'@z bigint OUTPUT', @z = @zero OUTPUT;
    EXEC sp_executesql N'SELECT @d = COUNT_BIG(*) FROM (SELECT CustomerProfileId FROM dbo.CustomerProfileOverrides GROUP BY CustomerProfileId HAVING COUNT(*) > 1) x;', N'@d bigint OUTPUT', @d = @dup OUTPUT;
END;
INSERT @R (CheckName, Expected, Actual, Status)
VALUES (N'CustomerProfileOverrides rows with an all-zero CustomerProfileId', N'0', CAST(@zero AS nvarchar(20)), CASE WHEN @zero = 0 THEN 'PASS' ELSE 'FAIL' END),
       (N'CustomerProfileOverrides duplicate CustomerProfileId', N'0', CAST(@dup AS nvarchar(20)), CASE WHEN @dup = 0 THEN 'PASS' ELSE 'FAIL' END);

-- 4 Collections
INSERT @R (CheckName, Expected, Actual, Status)
VALUES (N'dbo.fn_CollectionsUnitFloor exists (scalar)', N'exists', CASE WHEN OBJECT_ID(N'dbo.fn_CollectionsUnitFloor', N'FN') IS NULL THEN N'missing' ELSE N'exists' END,
        CASE WHEN OBJECT_ID(N'dbo.fn_CollectionsUnitFloor', N'FN') IS NULL THEN 'FAIL' ELSE 'PASS' END),
       (N'usp_CollectionsRcv_GetUnits has @Floors', N'present', CASE WHEN EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = OBJECT_ID(N'dbo.usp_CollectionsRcv_GetUnits') AND p.name = N'@Floors') THEN N'present' ELSE N'absent' END,
        CASE WHEN EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = OBJECT_ID(N'dbo.usp_CollectionsRcv_GetUnits') AND p.name = N'@Floors') THEN 'PASS' ELSE 'FAIL' END),
       (N'CollectionsTowers lookup index (UX_ or IX_, V001 creates one of them)', N'one of the two',
        ISNULL(STUFF((SELECT N', ' + i.name FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.CollectionsTowers') AND i.name IN (N'UX_CollectionsTowers_Company_TowerNumber', N'IX_CollectionsTowers_Company_TowerNumber') FOR XML PATH(N'')), 1, 2, N''), N'neither'),
        CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.CollectionsTowers') AND i.name IN (N'UX_CollectionsTowers_Company_TowerNumber', N'IX_CollectionsTowers_Company_TowerNumber')) THEN 'PASS' ELSE 'FAIL' END);
IF OBJECT_ID(N'dbo.fn_CollectionsUnitFloor', N'FN') IS NOT NULL
    INSERT @R (CheckName, Expected, Actual, Status)
    SELECT N'fn_CollectionsUnitFloor(' + t.u + N')', ISNULL(CAST(t.e AS nvarchar(5)), N'NULL'), ISNULL(CAST(dbo.fn_CollectionsUnitFloor(t.u) AS nvarchar(5)), N'NULL'),
           CASE WHEN ISNULL(dbo.fn_CollectionsUnitFloor(t.u), -1) = ISNULL(t.e, -1) THEN 'PASS' ELSE 'FAIL' END
    FROM (VALUES (N'TP140-101', 1), (N'TP140-1921', 19), (N'TP140-0507', 5), (N'TP130-601-A', 6), (N'TP136-C-402', 4), (N'TP120-601A', 6),
                 (N'TP140-P12', NULL), (N'TP140-12', NULL), (N'TP140-SHOP1', NULL), (N'TP140', NULL), (N'TP140-101-X7', NULL), (N'TP140-101-202', NULL)) t(u, e);

SELECT CASE WHEN SUM(CASE WHEN Status <> 'PASS' THEN 1 ELSE 0 END) = 0 THEN 'POST-DEPLOY CHECKS PASSED (' + CAST(COUNT(*) AS varchar(10)) + ' checks). Now run 99_Verify_TigerCS_UAT_ReadOnly.sql.'
            ELSE 'POST-DEPLOY CHECKS FAILED: ' + CAST(SUM(CASE WHEN Status <> 'PASS' THEN 1 ELSE 0 END) AS varchar(10)) + ' check(s) not PASS' END AS Verdict,
       SUM(CASE WHEN Status = 'PASS' THEN 1 ELSE 0 END) AS [PASS], SUM(CASE WHEN Status = 'FAIL' THEN 1 ELSE 0 END) AS [FAIL]
FROM @R;
SELECT CheckName, Expected, Actual, Status FROM @R ORDER BY CASE Status WHEN 'FAIL' THEN 1 ELSE 2 END, Id;
