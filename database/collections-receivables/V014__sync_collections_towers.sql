/*
  V014 - dbo.usp_CollectionsRcv_SyncTowers: keep dbo.CollectionsTowers in step with the towers the receivables snapshot really holds.

  Why: the tower picker (Collections -> Campaigns / Receivables) and every tower filter work on dbo.CollectionsTowers (TowerId is the LOCAL id the filter sends). That table is maintained by hand
  and, in production, is EMPTY, so the picker offered only "All projects" although the snapshot holds customers of many towers. tools/repair_tower_names.sql could not help: it only inserts
  a tower when CRM already holds a name for it.

  What the procedure does (idempotent; serialized by an application lock; one transaction):
    SOURCE   every distinct (CompanyId, TowerNumber) of the CURRENT published run of each company (4 and 32), from
             * dbo.CollectionsRcvLine        the unpaid instalments (TowerNumber = dbo.fn_CollectionsTowerNumber(UnitCode), stored by the refresh), and
             * dbo.CollectionsRcvAccountUnit the unit directory of the run (every unit PACT returned, settled ones included; cancelled '*' units are ignored).
    INSERT   no row for (CompanyId, TowerNumber) in dbo.CollectionsTowers -> one new ACTIVE row. TowerName = the CRM project name when CRM holds exactly one (dbo.fn_Collections_TowerNames),
             else the tower NUMBER itself - the existing "no name known" placeholder, which the pages show as the bare number and which is replaced once a real name exists. A name is never invented.
             Only plain numeric tower numbers (1-9 digits, no leading zero) are inserted; any other value is REPORTED, not written.
    RENAME   the row exists, its name is blank or only the number / TP<number>, and CRM holds exactly one name -> TowerName only.
    NEVER    delete a row, change TowerId / TowerNumber / CompanyId / IsActive, or overwrite a name that is already a real name (manually maintained names are kept).
  Duplicates are impossible: the match is on (CompanyId, TowerNumber) with the same normalisation every consumer uses, under the lock.

  @Apply = 1 (default, used by the application after each successful refresh) writes; @Apply = 0 is a DRY RUN: nothing is written and the plan is returned.
  Result set 1: Inserted, Renamed, SkippedNotNumeric, TowersInSnapshot, Applied.  Dry run only: result set 2 = the plan, one row per tower.

  Order: V001, V003, V010, V011, R001, V012, V013, THEN THIS SCRIPT (it only creates the procedure; it changes no data), R002 ... R004.  Needs R001 (snapshot tables) and V012 (fn_Collections_TowerNames).
  The first population of production data is done by tools/backfill_collections_towers.sql (dry run by default).
*/
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'dbo.CollectionsTowers', N'U') IS NULL
    THROW 50140, N'dbo.CollectionsTowers does not exist: run V001 first.', 1;
IF OBJECT_ID(N'dbo.fn_Collections_TowerNames', N'IF') IS NULL
    THROW 50141, N'dbo.fn_Collections_TowerNames does not exist: run V012 first.', 1;
GO
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_SyncTowers @Apply bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @inserted int = 0, @renamed int = 0, @skipped int = 0, @seen int = 0;

    BEGIN TRANSACTION;
        DECLARE @lock int;
        EXEC @lock = sys.sp_getapplock @Resource = N'CollectionsRcv_SyncTowers', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 60000;
        IF @lock < 0 THROW 50142, N'Another tower synchronisation is running; nothing was changed.', 1;

        -- 1. The towers the current published snapshot holds, per company.
        SELECT x.CompanyId, x.TowerNumber
          INTO #seen
          FROM (
                SELECT s.CompanyId, LTRIM(RTRIM(s.TowerNumber)) AS TowerNumber
                  FROM dbo.CollectionsRcvCompany c
                  JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
                 WHERE c.CurrentRunId IS NOT NULL
                UNION
                SELECT u.CompanyId, LTRIM(RTRIM(dbo.fn_CollectionsTowerNumber(u.UnitCode)))
                  FROM dbo.CollectionsRcvCompany c
                  JOIN dbo.CollectionsRcvAccountUnit u ON u.CompanyId = c.CompanyId AND u.RunId = c.CurrentRunId
                 WHERE c.CurrentRunId IS NOT NULL AND u.IsCancelled = 0
               ) x
         WHERE x.CompanyId IN (4, 32) AND x.TowerNumber IS NOT NULL AND x.TowerNumber <> N'';
        SET @seen = (SELECT COUNT(*) FROM #seen);

        -- 2. What each tower is today (stored row, if any) and what its name would be from the source.
        SELECT p.CompanyId, p.TowerNumber,
               CASE WHEN LEN(p.TowerNumber) BETWEEN 1 AND 9 AND p.TowerNumber NOT LIKE N'%[^0-9]%' AND LEFT(p.TowerNumber, 1) <> N'0' THEN 1 ELSE 0 END AS IsNumeric_,
               t.TowerId, t.IsActive, CONVERT(nvarchar(400), t.TowerName) AS StoredName,
               n.TowerName AS SourceName, n.NameSource
          INTO #plan
          FROM #seen p
          OUTER APPLY (SELECT TOP (1) x.TowerId, x.IsActive, x.TowerName FROM dbo.CollectionsTowers x
                        WHERE x.CompanyId = p.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), x.TowerNumber))) = p.TowerNumber
                        ORDER BY CASE WHEN x.IsActive = 1 THEN 0 ELSE 1 END, x.TowerId) t
          LEFT JOIN dbo.fn_Collections_TowerNames() n ON n.CompanyId = p.CompanyId AND n.TowerNumber = p.TowerNumber;

        SELECT *, CASE WHEN TowerId IS NULL AND IsNumeric_ = 1 THEN 'INSERT'
                       WHEN TowerId IS NULL THEN 'SKIP (not a plain numeric tower number)'
                       WHEN NameSource = 'Crm' THEN 'RENAME'
                       ELSE 'OK' END AS Action_
          INTO #act FROM #plan;

        SELECT @skipped = COUNT(*) FROM #act WHERE Action_ LIKE 'SKIP%';

        IF @Apply = 1
        BEGIN
            UPDATE t SET TowerName = LEFT(a.SourceName, 200)
              FROM dbo.CollectionsTowers t JOIN #act a ON a.TowerId = t.TowerId
             WHERE a.Action_ = 'RENAME'
               AND (LTRIM(RTRIM(CONVERT(nvarchar(400), t.TowerName))) = N''
                    OR UPPER(LTRIM(RTRIM(CONVERT(nvarchar(400), t.TowerName)))) IN (a.TowerNumber, N'TP' + a.TowerNumber, N'TP-' + a.TowerNumber));
            SET @renamed = @@ROWCOUNT;

            INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId, IsActive)
            SELECT a.TowerNumber, LEFT(CASE WHEN a.NameSource = 'Crm' THEN a.SourceName ELSE a.TowerNumber END, 200), a.CompanyId, 1
              FROM #act a
             WHERE a.Action_ = 'INSERT'
               AND NOT EXISTS (SELECT 1 FROM dbo.CollectionsTowers x WHERE x.CompanyId = a.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), x.TowerNumber))) = a.TowerNumber);
            SET @inserted = @@ROWCOUNT;
        END
        ELSE
        BEGIN
            SET @inserted = (SELECT COUNT(*) FROM #act WHERE Action_ = 'INSERT');
            SET @renamed = (SELECT COUNT(*) FROM #act WHERE Action_ = 'RENAME');
        END;

        SELECT @inserted AS Inserted, @renamed AS Renamed, @skipped AS SkippedNotNumeric, @seen AS TowersInSnapshot, CAST(@Apply AS bit) AS Applied;
        IF @Apply = 0
            SELECT CompanyId, TowerNumber, Action_, TowerId, IsActive, StoredName,
                   CASE WHEN Action_ = 'INSERT' THEN COALESCE(CASE WHEN NameSource = 'Crm' THEN SourceName END, TowerNumber)
                        WHEN Action_ = 'RENAME' THEN SourceName END AS NewName, NameSource
              FROM #act ORDER BY CompanyId, TRY_CONVERT(int, TowerNumber) DESC, TowerNumber;
    COMMIT TRANSACTION;
END;
GO
