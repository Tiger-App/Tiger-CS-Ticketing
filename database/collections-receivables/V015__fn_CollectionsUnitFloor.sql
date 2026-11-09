/*
  V015 - Apartment floor from a unit code.   (a function only: NO table, column or data changes)

  Neither PACT's receivables procedures, dbo.CollectionsRcvLine / CollectionsRcvAccountUnit, the review records nor the CRM unit-owner feed carry a floor column, so the Floor filter of the
  Collections Campaigns and Receivables pages reads the floor from the unit number. Mirrors TigerCS.Domain CollectionsUnitFloor.Parse exactly (unit-tested there):

    unit code = [TP]<tower>-<unit part>.  The unit part must hold EXACTLY ONE apartment number - 3 or 4 digits with an optional single trailing letter - and nothing else but single-letter
    block / suffix segments (at most 3 segments).  Floor = apartment number DIV 100.

      TP140-101 -> 1     TP140-1921 -> 19    TP140-0507 -> 5     TP130-601-A -> 6     TP136-C-402 -> 4     TP120-601A -> 6
      TP140-P12, TP140-12, TP140-SHOP1, TP140, TP140-101-X7, TP140-101-202, any code with '*'  ->  NULL   (not a recognisable apartment: never guessed)

  Used only by dbo.usp_CollectionsRcv_GetUnits when @Floors is given (R004). Deploy this BEFORE R004 (R004 only references the function when it runs with @Floors, but the procedure is validated at
  deployment time on some servers). Idempotent (CREATE OR ALTER). Deterministic and read-only.
*/
CREATE OR ALTER FUNCTION dbo.fn_CollectionsUnitFloor (@UnitCode nvarchar(200))
RETURNS int
AS
BEGIN
    DECLARE @s nvarchar(200) = LTRIM(RTRIM(@UnitCode));
    IF @s IS NULL OR @s = N'' OR @s LIKE N'%*%' RETURN NULL;
    IF UPPER(LEFT(@s, 2)) = N'TP' SET @s = LTRIM(SUBSTRING(@s, 3, 200));
    DECLARE @p int = CHARINDEX(N'-', @s);
    IF @p = 0 RETURN NULL;                                    -- a tower only, no unit part
    DECLARE @r nvarchar(200) = SUBSTRING(@s, @p + 1, 200);    -- the unit part after the tower
    DECLARE @apt nvarchar(10) = NULL, @tok nvarchar(200), @n int = 0;
    WHILE @r IS NOT NULL
    BEGIN
        SET @p = CHARINDEX(N'-', @r);
        IF @p = 0 BEGIN SET @tok = LTRIM(RTRIM(@r)); SET @r = NULL; END
        ELSE BEGIN SET @tok = LTRIM(RTRIM(LEFT(@r, @p - 1))); SET @r = SUBSTRING(@r, @p + 1, 200); END;
        SET @n += 1;
        IF @n > 3 RETURN NULL;
        IF @tok LIKE N'[0-9][0-9][0-9]' OR @tok LIKE N'[0-9][0-9][0-9][0-9]' OR @tok LIKE N'[0-9][0-9][0-9][A-Za-z]' OR @tok LIKE N'[0-9][0-9][0-9][0-9][A-Za-z]'
        BEGIN
            IF @apt IS NOT NULL RETURN NULL;                  -- two apartment numbers: ambiguous
            SET @apt = @tok;
        END
        ELSE IF NOT (LEN(@tok) = 1 AND @tok LIKE N'[A-Za-z]') RETURN NULL;
    END;
    IF @apt IS NULL RETURN NULL;
    RETURN CONVERT(int, LEFT(@apt, CASE WHEN @apt LIKE N'[0-9][0-9][0-9][0-9]%' THEN 4 ELSE 3 END)) / 100;
END;
GO
