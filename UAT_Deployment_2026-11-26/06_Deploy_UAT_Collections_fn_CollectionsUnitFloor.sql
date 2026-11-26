-- ############################################################################################
-- ## TigerCS UAT ONLY.   Database : TigerCsTicketing   (any other database aborts before any change)
-- ## NOT EXECUTED by Claude. Review, then run yourself in SSMS (SQLCMD mode OFF).
-- ############################################################################################
-- Object    : dbo.fn_CollectionsUnitFloor  (database/collections-receivables/V015__fn_CollectionsUnitFloor.sql, unchanged logic)
-- Why now   : the UAT verification found the function missing. dbo.usp_CollectionsRcv_GetUnits (R004) calls it when the Floor filter
--             (@Floors) is used, so that filter fails at run time until the function exists.
-- Effect    : creates (or re-creates, CREATE OR ALTER) one scalar function. NO table, column, index or data change. Deterministic and read-only.
--             An existing function is replaced only when it differs; the self-test below runs the repository's own examples.
-- HOW TO RUN: leave @DryRun = 1 (create + self-test + ROLLBACK), then set @DryRun = 0 (create + self-test + COMMIT).
--             Run as ONE batch. The function body is wrapped in EXEC(N'...') so the whole script is a single transactional batch (no GO).
-- ROLLBACK  : DROP FUNCTION dbo.fn_CollectionsUnitFloor;   (nothing depends on it at the schema level)
-- ############################################################################################
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 15000;

DECLARE @DryRun bit = 1;   -- 1 = create, self-test, ROLL BACK (default).  0 = create, self-test, COMMIT.
DECLARE @ExpectedDb sysname = N'TigerCsTicketing';
DECLARE @msg nvarchar(2000);

IF DB_NAME() IS NULL OR DB_NAME() <> @ExpectedDb
BEGIN
    SET @msg = N'ABORTED: connected to database [' + ISNULL(DB_NAME(), N'?') + N']; this UAT script only runs against [' + @ExpectedDb + N']. Nothing was changed.';
    THROW 50500, @msg, 1;
END;
PRINT N'Guard passed: server [' + CAST(@@SERVERNAME AS nvarchar(128)) + N'], database [' + DB_NAME() + N'], DryRun=' + CAST(@DryRun AS nvarchar(1)) + N'.';

-- Pre-check: the function's callers live in this database; refuse to continue in a database that has none of the Collections snapshot objects.
IF OBJECT_ID(N'dbo.usp_CollectionsRcv_GetUnits', N'P') IS NULL
    THROW 50501, 'ABORTED: dbo.usp_CollectionsRcv_GetUnits (R004) is missing - the Collections read procedures are not deployed here; deploy V001..R004 in the order given in database/collections-receivables/README.md first.', 1;

DECLARE @Existed bit = CASE WHEN OBJECT_ID(N'dbo.fn_CollectionsUnitFloor') IS NULL THEN 0 ELSE 1 END;
PRINT N'Pre-state: function exists = ' + CAST(@Existed AS nvarchar(1)) + N'.';

BEGIN TRY
    BEGIN TRANSACTION;

    EXEC(N'CREATE OR ALTER FUNCTION dbo.fn_CollectionsUnitFloor (@UnitCode nvarchar(200))
RETURNS int
AS
BEGIN
    DECLARE @s nvarchar(200) = LTRIM(RTRIM(@UnitCode));
    IF @s IS NULL OR @s = N'''' OR @s LIKE N''%*%'' RETURN NULL;
    IF UPPER(LEFT(@s, 2)) = N''TP'' SET @s = LTRIM(SUBSTRING(@s, 3, 200));
    DECLARE @p int = CHARINDEX(N''-'', @s);
    IF @p = 0 RETURN NULL;                                    -- a tower only, no unit part
    DECLARE @r nvarchar(200) = SUBSTRING(@s, @p + 1, 200);    -- the unit part after the tower
    DECLARE @apt nvarchar(10) = NULL, @tok nvarchar(200), @n int = 0;
    WHILE @r IS NOT NULL
    BEGIN
        SET @p = CHARINDEX(N''-'', @r);
        IF @p = 0 BEGIN SET @tok = LTRIM(RTRIM(@r)); SET @r = NULL; END
        ELSE BEGIN SET @tok = LTRIM(RTRIM(LEFT(@r, @p - 1))); SET @r = SUBSTRING(@r, @p + 1, 200); END;
        SET @n += 1;
        IF @n > 3 RETURN NULL;
        IF @tok LIKE N''[0-9][0-9][0-9]'' OR @tok LIKE N''[0-9][0-9][0-9][0-9]'' OR @tok LIKE N''[0-9][0-9][0-9][A-Za-z]'' OR @tok LIKE N''[0-9][0-9][0-9][0-9][A-Za-z]''
        BEGIN
            IF @apt IS NOT NULL RETURN NULL;                  -- two apartment numbers: ambiguous
            SET @apt = @tok;
        END
        ELSE IF NOT (LEN(@tok) = 1 AND @tok LIKE N''[A-Za-z]'') RETURN NULL;
    END;
    IF @apt IS NULL RETURN NULL;
    RETURN CONVERT(int, LEFT(@apt, CASE WHEN @apt LIKE N''[0-9][0-9][0-9][0-9]%'' THEN 4 ELSE 3 END)) / 100;
END;');
    PRINT N'dbo.fn_CollectionsUnitFloor created / updated.';

    -- Self-test (the examples documented in V015): any difference rolls everything back
    IF OBJECT_ID(N'dbo.fn_CollectionsUnitFloor', N'FN') IS NULL THROW 50510, 'VALIDATION FAILED: function not created as a scalar function.', 1;
    DECLARE @bad nvarchar(max) = N'';
    SELECT @bad = @bad + t.UnitCode + N' ' FROM (VALUES
        (N'TP140-101', 1), (N'TP140-1921', 19), (N'TP140-0507', 5), (N'TP130-601-A', 6), (N'TP136-C-402', 4), (N'TP120-601A', 6),
        (N'TP140-P12', NULL), (N'TP140-12', NULL), (N'TP140-SHOP1', NULL), (N'TP140', NULL), (N'TP140-101-X7', NULL), (N'TP140-101-202', NULL), (N'TP140-1*', NULL)
    ) t(UnitCode, ExpectedFloor)
    WHERE ISNULL(CAST(dbo.fn_CollectionsUnitFloor(t.UnitCode) AS int), -1) <> ISNULL(t.ExpectedFloor, -1);
    IF LEN(@bad) > 0
    BEGIN
        SET @msg = N'VALIDATION FAILED: self-test mismatch for: ' + @bad;
        THROW 50511, @msg, 1;
    END;
    IF OBJECTPROPERTY(OBJECT_ID(N'dbo.fn_CollectionsUnitFloor'), 'IsDeterministic') <> 1
        PRINT N'NOTE: IsDeterministic is not 1 (informational; V015 documents the function as deterministic in behaviour).';

    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'DRY RUN SUCCESS - the function compiled and passed its 13-case self-test inside a transaction that was ROLLED BACK. Nothing was changed. Set @DryRun = 0 to commit.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        PRINT N'COMMITTED. dbo.fn_CollectionsUnitFloor is in place.';
        SELECT o.name AS ObjectName, o.type_desc, o.create_date, o.modify_date FROM sys.objects o WHERE o.object_id = OBJECT_ID(N'dbo.fn_CollectionsUnitFloor');
        SELECT N'TP140-1921' AS UnitCode, dbo.fn_CollectionsUnitFloor(N'TP140-1921') AS Floor_expected_19;
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'FAILED - transaction rolled back, nothing was changed.';
    THROW;
END CATCH;
