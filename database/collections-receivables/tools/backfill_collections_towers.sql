/*
  backfill_collections_towers.sql - FIRST population of dbo.CollectionsTowers from the receivables snapshot (production backfill). DRY RUN BY DEFAULT.

  Situation: dbo.CollectionsTowers is empty, so Collections -> Campaigns / Receivables show only "All projects". This script fills it from the towers the CURRENT snapshot holds, through
  dbo.usp_CollectionsRcv_SyncTowers (V014) - the SAME procedure the application runs after every successful refresh, so the backfill and the permanent fix cannot disagree.

  Safe by construction:
    * @Apply = 0 (as shipped): reads only; prints the plan. Nothing is written.
    * @Apply = 1: one transaction, under an application lock. INSERTS missing (CompanyId, TowerNumber) towers (active) and fills a placeholder name from the single CRM project name; never deletes,
      never changes TowerId / TowerNumber / CompanyId / IsActive, never overwrites a real (manually maintained) name. Re-running changes nothing (idempotent, no duplicates).
    * A tower with no CRM name gets its NUMBER as the name (the pages show the bare number); give it a real name later with an UPDATE - the sync will not overwrite it.
    * Non-numeric / zero-padded tower numbers are reported, never inserted.

  Prerequisites: V001, V003, V012, V014 deployed; a published snapshot (tools/verify_snapshot.sql all OK). Back up first: SELECT * INTO dbo.Bak_CollectionsTowers_<yyyymmdd> FROM dbo.CollectionsTowers;
  The application's tower list is cached for 60 seconds: the dropdown shows the new towers within a minute of @Apply = 1.
*/
SET NOCOUNT ON;
DECLARE @Apply bit = 0;      -- <== set to 1 to write

IF OBJECT_ID(N'dbo.usp_CollectionsRcv_SyncTowers', N'P') IS NULL THROW 50143, N'Deploy V014__sync_collections_towers.sql first.', 1;

PRINT N'--- 0. Before: dbo.CollectionsTowers ---';
SELECT COUNT(*) AS Rows_, SUM(CASE WHEN IsActive = 1 THEN 1 ELSE 0 END) AS ActiveRows FROM dbo.CollectionsTowers;

PRINT N'--- 1. Towers in the current published snapshot, per company ---';
SELECT s.CompanyId, COUNT(DISTINCT LTRIM(RTRIM(s.TowerNumber))) AS DistinctTowers, COUNT(*) AS Lines
  FROM dbo.CollectionsRcvCompany c JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
 WHERE c.CurrentRunId IS NOT NULL GROUP BY s.CompanyId;

PRINT N'--- 2. Sync (summary; then, in a dry run, the plan per tower) ---';
EXEC dbo.usp_CollectionsRcv_SyncTowers @Apply = @Apply;

PRINT N'--- 3. After: dbo.CollectionsTowers as the picker will list it (newest tower first) ---';
EXEC dbo.usp_CollectionsRcv_GetTowers;

PRINT N'--- 4. Consistency: towers with customers in the snapshot but NOT in the picker (must be empty after @Apply = 1) ---';
SELECT DISTINCT s.CompanyId, LTRIM(RTRIM(s.TowerNumber)) AS TowerNumber
  FROM dbo.CollectionsRcvCompany c JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
 WHERE c.CurrentRunId IS NOT NULL AND s.TowerNumber IS NOT NULL AND LTRIM(RTRIM(s.TowerNumber)) <> N''
   AND NOT EXISTS (SELECT 1 FROM dbo.CollectionsTowers t WHERE t.IsActive = 1 AND t.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = LTRIM(RTRIM(s.TowerNumber)))
 ORDER BY 1, 2;
IF @Apply = 0 PRINT N'DRY RUN: nothing was written. Set @Apply = 1 to apply.';
