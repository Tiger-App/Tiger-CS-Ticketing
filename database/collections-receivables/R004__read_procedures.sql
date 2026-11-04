/*
  R004 - Read procedures of the rebuilt receivables snapshot.  The application NEVER calls PACT for a page view; it reads these.

    dbo.usp_CollectionsRcv_GetUnits   RECEIVABLES page (and the unit list behind it): one row per apartment of a customer, its payments, filters, totals, paging
    dbo.usp_CollectionsRcv_GetLines   payment rows of a due-date window (used by Campaigns and the Review workflow, whose rules run in the application)
    dbo.usp_CollectionsRcv_GetUnit    Payment Summary: one unit by its normalised key (tower + unit code), never by a phone number
    dbo.usp_CollectionsRcv_GetTowers  active towers for the dropdown

  SEMANTICS (all dates are calendar days; @Today is today in Asia/Dubai, passed by the application; the reference is its CALENDAR MONTH, never the day)
      @MonthStart = first day of @Today's month, @NextMonthStart = first day of the following month (December rolls into January):
        Overdue = DueDate <  @MonthStart
        Due     = DueDate >= @MonthStart AND DueDate < @NextMonthStart      (any day of the current month: past, today or still to come)
        DueDate >= @NextMonthStart is NEVER read, listed, totalled or exported.
    * Only unpaid or partly paid payments exist (remaining Amount > 0; the amount is the REMAINING amount).  Total = Due + Overdue = the sum of the payments the details list.
    * @FromDate / @ToDate only NARROW the DUE-date window (NULL = no bound; @ToDate is capped at the last day of the current month).  A Month/Year filter passes that month's
      first and last day; it never moves @MonthStart: a payment is Due / Overdue against the current month whatever window is chosen.  No window = historic overdue + the whole current month.
    * @Status: all (Due + Overdue, the default) | due | overdue - applied to the PAYMENTS, so every figure below is computed from the payments that match.
    * @MinTotal (NULL = none): applied AFTER the payments of one apartment are summed (Total > @MinTotal, strictly greater), before the counts, paging and export.
    * Excluded before aggregation and paging: unit code containing '*', UnitID <= 0, unit code blank or 0.  Only that apartment is dropped.
    * An apartment is (CompanyId, TenantId, UnitId, UnitCode), compared binary-exactly: other companies, customers and apartments are never merged.
    * The apartment's CONTACT (name / mobile / e-mail as PACT holds them) is that of its earliest matching payment (then smallest voucher, then id) - the same representative row Campaigns use,
      so the two pages never show different PACT contacts for one apartment.
    * ORDER: the NEWEST TOWER FIRST - the tower NUMBER as an integer, descending (TP140, TP132, TP131 ... TP119; never the text, never the name; a non-numeric tower number after all numeric ones),
      then the units of a tower by their unit number ascending (numeric unit numbers before lettered ones, then the unit code text), then company, customer, unit id - a TOTAL order computed over the whole
      filtered set BEFORE the page is cut, so pages never overlap or skip (mirrored by TigerCS.Domain CollectionsTowerOrder); payments by due date DESC, then voucher, then id.
    * TOWER NAMES come from dbo.fn_Collections_TowerNames (V012): CollectionsTowers when its name is a real name, else the single name CRM holds for that company's tower. NULL = no name known (the page shows the number).
    * TOWER FILTER (@TowerId / @TowerIds): none = every tower; otherwise a payment is kept when its (company, tower number) is ANY of the chosen towers (OR), decided on the payments BEFORE they are summed per apartment,
      so totals, counts and paging cover exactly the chosen towers and an apartment can never appear twice. One scan of the snapshot whatever the number of towers.
    * Search: customer name, mobile, e-mail, customer id, tower number / name, apartment, voucher, project - PACT contact data AND the linked CRM contact.
  Result sets of usp_CollectionsRcv_GetUnits (a contract with RcvReceivablesSource):
    1 scope     ScopeValid, TowerId, CompanyId, TowerNumber, TowerName   (ScopeValid = 0: unknown tower or tower/company clash; nothing else is returned; TowerNumber / TowerName only when ONE tower is chosen)
    2 status    usp_CollectionsRcv_GetStatus for the companies in scope
    3 totals    UnitCount, LineCount, DueLineCount, OverdueLineCount, DueTotal, OverdueTotal, Total   (over the whole filtered set, before paging)
    4 units     the requested page
    5 lines     the payments of exactly those units (sum per unit = its Total)
*/
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetUnits
    @Today       date,
    @CompanyId   int           = NULL,
    @TowerId     int           = NULL,
    @FromDate    date          = NULL,
    @ToDate      date          = NULL,
    @Status      varchar(10)   = 'all',
    @Search      nvarchar(200) = NULL,
    @PhoneDigits nvarchar(200) = NULL,
    @MinTotal    decimal(19,4) = NULL,
    @PageNumber  int           = 1,
    @PageSize    int           = 25,
    @Only        tinyint       = NULL,     -- NULL = all five result sets; 3, 4 or 5 = ONLY that one (so tools/verify_details_total.sql can INSERT ... EXEC it and compare the units with their payments)
    @TowerIds    nvarchar(max) = NULL      -- comma-separated local tower ids (several towers, OR); combined with @TowerId; NULL / empty with @TowerId NULL = all towers
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @Only IS NOT NULL AND @Only NOT IN (3, 4, 5) THROW 50036, N'@Only must be NULL, 3, 4 or 5.', 1;
    IF @Status NOT IN ('all', 'due', 'overdue') THROW 50035, N'Unknown status filter.', 1;
    IF @FromDate IS NOT NULL AND @ToDate IS NOT NULL AND @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    IF @PageNumber < 1 OR @PageSize < 1 OR @PageSize > 100 THROW 50034, N'Invalid page.', 1;

    CREATE TABLE #tn (CompanyId int NOT NULL, TowerNumber nvarchar(20) NOT NULL, TowerName nvarchar(400) NOT NULL, PRIMARY KEY (CompanyId, TowerNumber));
    INSERT #tn (CompanyId, TowerNumber, TowerName) SELECT CompanyId, TowerNumber, TowerName FROM dbo.fn_Collections_TowerNames();
    -- The chosen towers: @TowerId plus every id of @TowerIds, ONE set. A tower id that is unknown (or not a number) makes the whole selection invalid (ScopeValid = 0), exactly like an unknown single tower.
    DECLARE @Raw TABLE (Id int NULL);
    IF @TowerId IS NOT NULL INSERT @Raw VALUES (@TowerId);
    IF NULLIF(LTRIM(RTRIM(@TowerIds)), N'') IS NOT NULL
        INSERT @Raw SELECT TRY_CONVERT(int, LTRIM(RTRIM(value))) FROM STRING_SPLIT(@TowerIds, N',') WHERE LTRIM(RTRIM(value)) <> N'';
    DECLARE @Wanted TABLE (TowerId int NOT NULL PRIMARY KEY);
    INSERT @Wanted SELECT DISTINCT Id FROM @Raw WHERE Id IS NOT NULL;
    DECLARE @HasTowers bit = CASE WHEN EXISTS (SELECT 1 FROM @Raw) THEN 1 ELSE 0 END;
    DECLARE @TowerFound bit = 0, @TowerCompany int, @TowerNumber nvarchar(20), @TowerName nvarchar(400);
    -- (CompanyId, TowerNumber) of the chosen towers; a table VARIABLE so its collation matches the snapshot columns it is compared with.
    DECLARE @Sel TABLE (CompanyId int NOT NULL, TowerNumber nvarchar(20) NOT NULL, PRIMARY KEY (CompanyId, TowerNumber));
    INSERT @Sel SELECT DISTINCT t.CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) FROM dbo.CollectionsTowers t JOIN @Wanted w ON w.TowerId = t.TowerId;
    IF @HasTowers = 1 AND NOT EXISTS (SELECT 1 FROM @Raw WHERE Id IS NULL) AND (SELECT COUNT(*) FROM @Wanted) = (SELECT COUNT(*) FROM dbo.CollectionsTowers t JOIN @Wanted w ON w.TowerId = t.TowerId)
        SET @TowerFound = 1;
    IF (SELECT COUNT(*) FROM @Sel) = 1
    BEGIN
        SELECT @TowerCompany = s.CompanyId, @TowerNumber = s.TowerNumber, @TowerName = (SELECT TOP (1) CONVERT(nvarchar(400), t.TowerName) FROM dbo.CollectionsTowers t JOIN @Wanted w ON w.TowerId = t.TowerId) FROM @Sel s;
    END
    ELSE IF EXISTS (SELECT 1 FROM @Sel) AND (SELECT COUNT(DISTINCT CompanyId) FROM @Sel) = 1
    BEGIN
        SELECT @TowerCompany = MIN(CompanyId) FROM @Sel;
    END;
    SELECT @TowerName = n.TowerName FROM #tn n WHERE n.CompanyId = @TowerCompany AND n.TowerNumber = @TowerNumber;
    DECLARE @Scope int = COALESCE(@TowerCompany, @CompanyId);
    DECLARE @ScopeValid bit = CASE WHEN @HasTowers = 0 THEN 1
                                   WHEN @TowerFound = 1 AND (@CompanyId IS NULL OR NOT EXISTS (SELECT 1 FROM @Sel WHERE CompanyId <> @CompanyId)) THEN 1 ELSE 0 END;
    IF @Only IS NULL SELECT @ScopeValid AS ScopeValid, @TowerId AS TowerId, @TowerCompany AS CompanyId, @TowerNumber AS TowerNumber, @TowerName AS TowerName;
    IF @ScopeValid = 0
    BEGIN
        IF @Only IS NULL
        BEGIN
            SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
            SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        END;
        RETURN;
    END;
    IF @Only IS NULL EXEC dbo.usp_CollectionsRcv_GetStatus @CompanyId = @Scope;

    DECLARE @Like nvarchar(420) = NULL;
    IF NULLIF(LTRIM(RTRIM(@Search)), N'') IS NOT NULL
        SET @Like = N'%' + REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@Search)), N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';
    DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1);
    DECLARE @NextMonthStart date = DATEADD(MONTH, 1, @MonthStart);
    DECLARE @LastExclusive date = CASE WHEN @ToDate IS NULL OR @ToDate >= @NextMonthStart THEN @NextMonthStart ELSE DATEADD(DAY, 1, @ToDate) END;   -- nothing from the next month on is ever read

    -- Towers whose NAME matches the search (a handful of rows), so the payment scan below does a keyed lookup instead of a correlated subquery per payment.
    DECLARE @TowerHits TABLE (CompanyId int NOT NULL, TowerNumber nvarchar(20) NOT NULL, PRIMARY KEY (CompanyId, TowerNumber));
    IF @Like IS NOT NULL
        INSERT @TowerHits SELECT tq.CompanyId, tq.TowerNumber FROM #tn tq WHERE tq.TowerName LIKE @Like ESCAPE N'\';

    -- 1. The payments in play (everything except search and Minimum Total), narrow.
    CREATE TABLE #l
    (
        LineId bigint NOT NULL PRIMARY KEY, CompanyId int NOT NULL, RunId uniqueidentifier NOT NULL, TenantId nvarchar(100) COLLATE Latin1_General_BIN2 NOT NULL, UnitId bigint NOT NULL,
        UnitCode nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL, DueDate date NOT NULL, Amount decimal(19,4) NOT NULL, Hit bit NOT NULL, TowerNumber nvarchar(20) NULL
    );
    INSERT #l (LineId, CompanyId, RunId, TenantId, UnitId, UnitCode, DueDate, Amount, Hit, TowerNumber)
    SELECT s.LineId, s.CompanyId, s.RunId, s.TenantId, s.UnitId, s.UnitCode, s.DueDate, s.Amount,
           CASE WHEN @Like IS NULL THEN 1
                WHEN s.FullName LIKE @Like ESCAPE N'\' OR s.TenantId COLLATE DATABASE_DEFAULT LIKE @Like ESCAPE N'\' OR s.Email LIKE @Like ESCAPE N'\' OR s.Mobile LIKE @Like ESCAPE N'\'
                  OR s.UnitCode COLLATE DATABASE_DEFAULT LIKE @Like ESCAPE N'\' OR s.VoucherNumber LIKE @Like ESCAPE N'\' OR s.TowerNumber LIKE @Like ESCAPE N'\' OR s.ProjectCode LIKE @Like ESCAPE N'\'
                  OR EXISTS (SELECT 1 FROM @TowerHits h WHERE h.CompanyId = s.CompanyId AND h.TowerNumber = s.TowerNumber)
                  OR (@PhoneDigits IS NOT NULL AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Mobile, N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') LIKE N'%' + @PhoneDigits + N'%')
                THEN 1 ELSE 0 END,
           s.TowerNumber
      FROM dbo.CollectionsRcvCompany c
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
     WHERE c.CurrentRunId IS NOT NULL AND (@Scope IS NULL OR c.CompanyId = @Scope)
       AND (@HasTowers = 0 OR EXISTS (SELECT 1 FROM @Sel x WHERE x.CompanyId = s.CompanyId AND x.TowerNumber = s.TowerNumber))   -- OR across the chosen towers, before aggregation and paging
       AND s.DueDate < @LastExclusive AND (@FromDate IS NULL OR s.DueDate >= @FromDate)
       AND (@Status = 'all' OR (@Status = 'due' AND s.DueDate >= @MonthStart) OR (@Status = 'overdue' AND s.DueDate < @MonthStart))
       AND s.Amount > 0 AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
    OPTION (RECOMPILE);

    -- 2. One row per apartment.
    CREATE TABLE #u
    (
        CompanyId int NOT NULL, RunId uniqueidentifier NOT NULL, TenantId nvarchar(100) COLLATE Latin1_General_BIN2 NOT NULL, UnitId bigint NOT NULL, UnitCode nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
        Due decimal(19,4) NOT NULL, Overdue decimal(19,4) NOT NULL, Total decimal(19,4) NOT NULL, Cnt int NOT NULL, DueCnt int NOT NULL, OverCnt int NOT NULL, Latest date NOT NULL, Hit bit NOT NULL,
        TowerNumber nvarchar(20) NULL
    );
    INSERT #u
    SELECT CompanyId, RunId, TenantId, UnitId, UnitCode,
           SUM(CASE WHEN DueDate >= @MonthStart THEN Amount ELSE 0 END), SUM(CASE WHEN DueDate < @MonthStart THEN Amount ELSE 0 END), SUM(Amount), COUNT(*),
           SUM(CASE WHEN DueDate >= @MonthStart THEN 1 ELSE 0 END), SUM(CASE WHEN DueDate < @MonthStart THEN 1 ELSE 0 END), MAX(DueDate), MAX(CAST(Hit AS int)), MAX(TowerNumber)
      FROM #l GROUP BY CompanyId, RunId, TenantId, UnitId, UnitCode;

    -- Search also matches the linked CRM contact (CRM first, PACT completing): keep an apartment whose payments matched OR whose CRM customer does.
    IF @Like IS NOT NULL
    BEGIN
        SELECT l.CompanyId, l.UnitKey, l.FullName, l.PhoneNorm, l.EmailNorm INTO #crm FROM dbo.fn_Collections_CrmUnitLinks() l WHERE l.Customers = 1 AND l.CompanyAmbiguous = 0;
        DELETE u FROM #u u
         WHERE u.Hit = 0 AND NOT EXISTS (SELECT 1 FROM #crm c
                                          WHERE c.CompanyId = u.CompanyId AND c.UnitKey = dbo.fn_CollectionsUnitKey(u.UnitCode) COLLATE Latin1_General_BIN2
                                            AND (c.FullName LIKE @Like ESCAPE N'\' OR c.PhoneNorm LIKE @Like ESCAPE N'\' OR c.EmailNorm LIKE @Like ESCAPE N'\'
                                                 OR (@PhoneDigits IS NOT NULL AND REPLACE(c.PhoneNorm, N'+', N'') LIKE N'%' + @PhoneDigits + N'%')));
    END;
    -- Minimum Total: on the apartment's SUM, strictly greater, before totals, counts and paging.
    IF @MinTotal IS NOT NULL DELETE FROM #u WHERE Total <= @MinTotal;

    IF @Only IS NULL OR @Only = 3
    SELECT COUNT(*) AS UnitCount, ISNULL(SUM(Cnt), 0) AS LineCount, ISNULL(SUM(DueCnt), 0) AS DueLineCount, ISNULL(SUM(OverCnt), 0) AS OverdueLineCount,
           ISNULL(SUM(Due), 0) AS DueTotal, ISNULL(SUM(Overdue), 0) AS OverdueTotal, ISNULL(SUM(Total), 0) AS Total FROM #u;

    -- 3. The requested page. The order is decided over ALL the filtered apartments BEFORE the page is cut: newest tower first (tower number as an integer, descending; a non-numeric tower
    --    number after the numeric ones), then the apartment inside its tower (unit number ascending, numeric before lettered, then the unit code), then company / customer / unit id: a total order.
    SELECT * INTO #p FROM
    (
        SELECT u.*, ROW_NUMBER() OVER (ORDER BY
                   CASE WHEN k.TowerSort IS NULL THEN 1 ELSE 0 END, k.TowerSort DESC,
                   CASE WHEN k.TowerSort IS NULL THEN u.TowerNumber COLLATE Latin1_General_BIN2 END,
                   CASE WHEN k2.UnitSort IS NULL THEN 1 ELSE 0 END, k2.UnitSort,
                   k.UnitRest, u.UnitCode, u.CompanyId, u.TenantId, u.UnitId) AS Rn
          FROM #u u
          CROSS APPLY (SELECT CASE WHEN LEN(LTRIM(RTRIM(u.TowerNumber))) BETWEEN 1 AND 9 AND LTRIM(RTRIM(u.TowerNumber)) NOT LIKE N'%[^0-9]%' THEN CONVERT(int, LTRIM(RTRIM(u.TowerNumber))) END AS TowerSort,
                              LTRIM(RTRIM(CASE WHEN CHARINDEX(N'-', u.UnitCode) > 0 THEN SUBSTRING(u.UnitCode, CHARINDEX(N'-', u.UnitCode) + 1, 200) ELSE u.UnitCode END)) AS UnitRest) k
          CROSS APPLY (SELECT CASE WHEN LEN(k.UnitRest) BETWEEN 1 AND 15 AND k.UnitRest NOT LIKE N'%[^0-9]%' THEN CONVERT(bigint, k.UnitRest) END AS UnitSort) k2
    ) x WHERE x.Rn > (@PageNumber - 1) * @PageSize AND x.Rn <= @PageNumber * @PageSize;

    IF @Only IS NULL OR @Only = 4
    SELECT p.CompanyId, p.TenantId COLLATE DATABASE_DEFAULT AS TenantId, p.UnitId, p.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, r.FullName, r.TowerNumber,
           tw.TowerName AS TowerName, r.ProjectCode, p.Due, p.Overdue, p.Total, p.Cnt AS InstalmentCount, p.Latest AS LatestDueDate,
           -- The CRM side of the apartment (V010 / V012; NULL = CRM holds no eligible sale or is not loaded): 1 customer -> that contact, > 1 -> ambiguous, nobody chosen; the sale of a unit that
           -- BOTH companies hold is tied to neither (CrmCompanyAmbiguous = 1, no CRM contact is used).
           l.Customers AS CrmCustomers, CASE WHEN l.Customers = 1 AND l.CompanyAmbiguous = 0 THEN l.CustomerId END AS CrmCustomerId, CASE WHEN l.Customers = 1 AND l.CompanyAmbiguous = 0 THEN l.FullName END AS CrmName,
           CASE WHEN l.Customers = 1 AND l.CompanyAmbiguous = 0 THEN l.PhoneNorm END AS CrmPhone, CASE WHEN l.Customers = 1 AND l.CompanyAmbiguous = 0 THEN l.EmailNorm END AS CrmEmail,
           CAST(CASE WHEN l.Customers = 1 AND l.CompanyAmbiguous = 1 THEN 1 ELSE 0 END AS int) AS CrmCompanyAmbiguous
      FROM #p p
      CROSS APPLY (SELECT TOP (1) s.* FROM dbo.CollectionsRcvLine s
                    WHERE s.CompanyId = p.CompanyId AND s.RunId = p.RunId AND s.TenantId = p.TenantId AND s.UnitId = p.UnitId AND s.UnitCode = p.UnitCode
                    ORDER BY s.DueDate, s.VoucherNumber, s.LineId) r
      LEFT JOIN dbo.fn_Collections_CrmUnitLinks() l ON l.CompanyId = p.CompanyId AND l.UnitKey = dbo.fn_CollectionsUnitKey(p.UnitCode) COLLATE Latin1_General_BIN2
      LEFT JOIN #tn tw ON tw.CompanyId = r.CompanyId AND tw.TowerNumber = r.TowerNumber
     ORDER BY p.Rn;

    -- 4. Exactly the matching payments of those apartments (the same #l set, so the sum of a unit's rows is its Total).
    IF @Only IS NULL OR @Only = 5
    SELECT s.CompanyId, s.TenantId COLLATE DATABASE_DEFAULT AS TenantId, s.UnitId, s.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, s.VoucherNumber, s.ChequeNumber, s.DueDate,
           s.Amount AS RemainingAmount, s.Mobile, s.Email, s.SourceStatus,
           CASE WHEN s.DueDate < @MonthStart THEN 'Overdue' ELSE 'Due' END AS Classification
      FROM #p p
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = p.CompanyId AND s.RunId = p.RunId AND s.TenantId = p.TenantId AND s.UnitId = p.UnitId
      JOIN #l l ON l.LineId = s.LineId
     WHERE s.UnitCode = p.UnitCode
     ORDER BY p.Rn, s.DueDate DESC, s.VoucherNumber COLLATE Latin1_General_BIN2, s.LineId
    OPTION (RECOMPILE);
END;
GO

/*
  Payment rows of a due-date window for the Campaigns evaluation and the Review workflow.  Same exclusions as above (unpaid only, never a cancelled / invalid
  apartment).  The window is the CALLER's: Campaigns pass a window that ends at the end of the current month (Dubai), the Review workflow its month end.
  This procedure applies NO Due / Overdue classification: the application classifies the rows against the current month (CollectionsDueMonth).  @MinAmount keeps payments whose remaining
  amount is >= it (0 = every unpaid payment).
    1 scope   ScopeValid, TowerId, CompanyId, TowerNumber, TowerName
    2 status  usp_CollectionsRcv_GetStatus for the companies in scope
    3 lines   one row per payment
*/
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetLines
    @CompanyId int           = NULL,
    @TowerId   int           = NULL,
    @FromDate  date          = NULL,
    @ToDate    date          = NULL,
    @MinAmount decimal(19,4) = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @FromDate IS NOT NULL AND @ToDate IS NOT NULL AND @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    CREATE TABLE #tn (CompanyId int NOT NULL, TowerNumber nvarchar(20) NOT NULL, TowerName nvarchar(400) NOT NULL, PRIMARY KEY (CompanyId, TowerNumber));
    INSERT #tn (CompanyId, TowerNumber, TowerName) SELECT CompanyId, TowerNumber, TowerName FROM dbo.fn_Collections_TowerNames();
    DECLARE @TowerFound bit = 0, @TowerCompany int, @TowerNumber nvarchar(20), @TowerName nvarchar(400);
    IF @TowerId IS NOT NULL
        SELECT @TowerFound = 1, @TowerCompany = t.CompanyId, @TowerNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))), @TowerName = CONVERT(nvarchar(400), t.TowerName)
          FROM dbo.CollectionsTowers t WHERE t.TowerId = @TowerId;
    SELECT @TowerName = n.TowerName FROM #tn n WHERE n.CompanyId = @TowerCompany AND n.TowerNumber = @TowerNumber;
    DECLARE @Scope int = COALESCE(@TowerCompany, @CompanyId);
    DECLARE @ScopeValid bit = CASE WHEN @TowerId IS NULL THEN 1 WHEN @TowerFound = 1 AND (@CompanyId IS NULL OR @CompanyId = @TowerCompany) THEN 1 ELSE 0 END;
    SELECT @ScopeValid AS ScopeValid, @TowerId AS TowerId, @TowerCompany AS CompanyId, @TowerNumber AS TowerNumber, @TowerName AS TowerName;
    IF @ScopeValid = 0
    BEGIN
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        RETURN;
    END;
    EXEC dbo.usp_CollectionsRcv_GetStatus @CompanyId = @Scope;

    SELECT s.CompanyId, s.TenantId COLLATE DATABASE_DEFAULT AS TenantId, s.FullName, s.Mobile, s.Email, s.UnitId, s.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, s.ProjectCode,
           s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount, s.SourceStatus, s.TowerNumber, tw.TowerName AS TowerName
      FROM dbo.CollectionsRcvCompany c
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
      LEFT JOIN #tn tw ON tw.CompanyId = s.CompanyId AND tw.TowerNumber = s.TowerNumber
     WHERE c.CurrentRunId IS NOT NULL AND (@Scope IS NULL OR c.CompanyId = @Scope)
       AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
       AND (@ToDate IS NULL OR s.DueDate <= @ToDate) AND (@FromDate IS NULL OR s.DueDate >= @FromDate)
       AND s.Amount > 0 AND s.Amount >= @MinAmount AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
     ORDER BY s.CompanyId, s.TenantId, s.UnitId, s.DueDate, s.VoucherNumber, s.LineId   -- the order the evaluation takes its representative row from: earliest due date, then voucher
    OPTION (RECOMPILE);
END;
GO

/*
  Payment Summary by unit.  @UnitKey is the normalised key (TP140-101); @CompanyId narrows to one company (NULL = every company that holds the unit).
    1 status        usp_CollectionsRcv_GetStatus
    2 identities    per (company, tenant, unit): the PACT customer of the unit (name / mobile / e-mail as PACT holds them - mobile may be empty), tower, payment count
    3 instalments   ALL the unit's unpaid payments, future months included, newest first. The application classifies them with the one month rule (CollectionsDueMonth: Overdue < first day of the month of @AsOfDate, Due inside it, later = Not yet due) - @AsOfDate is only that reference date
    4 crm           the CRM link of the unit (usp_Collections_GetCrmUnitLinks shape), all companies
  Rows with UnitID <= 0 or a cancelled ('*') / blank / 0 code never match.
*/
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetUnit
    @UnitKey   nvarchar(220),
    @CompanyId int = NULL,
    @AsOfDate  date,
    @Only      tinyint = NULL      -- NULL = all four result sets (what the application reads); 3 = ONLY the instalments, so tools/verify_unit_payment_summary.sql can INSERT ... EXEC it
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @Only IS NOT NULL AND @Only <> 3 THROW 50036, N'@Only must be NULL or 3.', 1;
    DECLARE @Bare nvarchar(220) = SUBSTRING(@UnitKey, 3, 220);
    IF @Only IS NULL EXEC dbo.usp_CollectionsRcv_GetStatus @CompanyId = @CompanyId;
    CREATE TABLE #tn (CompanyId int NOT NULL, TowerNumber nvarchar(20) NOT NULL, TowerName nvarchar(400) NOT NULL, PRIMARY KEY (CompanyId, TowerNumber));
    INSERT #tn (CompanyId, TowerNumber, TowerName) SELECT CompanyId, TowerNumber, TowerName FROM dbo.fn_Collections_TowerNames();

    SELECT s.CompanyId, s.RunId, s.TenantId, s.UnitId, s.UnitCode, COUNT(*) AS Cnt, MAX(s.DueDate) AS Latest
      INTO #k
      FROM dbo.CollectionsRcvCompany c
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
     WHERE c.CurrentRunId IS NOT NULL AND (@CompanyId IS NULL OR c.CompanyId = @CompanyId)
       AND UPPER(LTRIM(RTRIM(s.UnitCode))) COLLATE Latin1_General_BIN2 IN (@UnitKey COLLATE Latin1_General_BIN2, @Bare COLLATE Latin1_General_BIN2)
       AND s.UnitId > 0 AND s.UnitCode NOT LIKE N'%*%' AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.Amount > 0
     GROUP BY s.CompanyId, s.RunId, s.TenantId, s.UnitId, s.UnitCode
    OPTION (RECOMPILE);

    IF @Only IS NULL
    SELECT k.CompanyId, k.TenantId COLLATE DATABASE_DEFAULT AS TenantId, k.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, k.UnitId, r.FullName, r.Mobile, r.Email, r.TowerNumber,
           tw.TowerName AS TowerName, k.Cnt AS AllRows, k.Cnt AS OpenRows
      FROM #k k
      CROSS APPLY (SELECT TOP (1) s.* FROM dbo.CollectionsRcvLine s
                    WHERE s.CompanyId = k.CompanyId AND s.RunId = k.RunId AND s.TenantId = k.TenantId AND s.UnitId = k.UnitId AND s.UnitCode = k.UnitCode
                    ORDER BY s.DueDate, s.VoucherNumber, s.LineId) r
      LEFT JOIN #tn tw ON tw.CompanyId = r.CompanyId AND tw.TowerNumber = r.TowerNumber
     ORDER BY k.CompanyId, k.TenantId, k.UnitCode, k.UnitId;

    SELECT s.CompanyId, s.TenantId COLLATE DATABASE_DEFAULT AS TenantId, s.UnitId, s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount AS RemainingAmount, s.SourceStatus
      FROM #k k
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = k.CompanyId AND s.RunId = k.RunId AND s.TenantId = k.TenantId AND s.UnitId = k.UnitId
     WHERE s.UnitCode = k.UnitCode AND s.Amount > 0   -- ALL unpaid instalments of the unit, future ones too: the Payment tab's Remaining Balance and its Not yet due rows; the application classifies by date
     ORDER BY k.CompanyId, k.TenantId, k.UnitId, s.DueDate DESC, s.VoucherNumber COLLATE Latin1_General_BIN2, s.LineId;

    IF @Only IS NULL
    SELECT l.CompanyId, l.UnitKey, l.Customers, l.CustomerId, l.CrmUnitId, l.LeadId, l.FullName, l.PhoneNorm, l.EmailNorm, l.CompanyAmbiguous
      FROM dbo.fn_Collections_CrmUnitLinks() l
     WHERE l.UnitKey = @UnitKey COLLATE Latin1_General_BIN2 AND (@CompanyId IS NULL OR l.CompanyId = @CompanyId);
END;
GO

/*
  usp_CollectionsRcv_GetUnitAccounts - the unit/account DIRECTORY entry of a unit key, fully paid units included (CollectionsRcvAccountUnit of the CURRENT run of each company):
    1 the matching (company, tenant, unit id, unit code) rows of the unit key (cancelled '*' codes never match, the mark is never removed)
    2 EVERY unit of the same (company, tenant) accounts (settled and cancelled ones included, flagged), so an account that also covers a fully paid sibling is visible
    3 the as-of time of the directory per company
  The directory holds what PACT's receivables procedure returned; it is not proven to be every unit of the tenant (see the table's comment).
*/
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetUnitAccounts
    @UnitKey   nvarchar(220),
    @CompanyId int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    DECLARE @Bare nvarchar(220) = SUBSTRING(@UnitKey, 3, 220);
    SELECT a.RunId, a.CompanyId, a.TenantId, a.UnitId, a.UnitCode, a.OpenRows, a.SettledRows
      INTO #m
      FROM dbo.CollectionsRcvCompany c
      JOIN dbo.CollectionsRcvAccountUnit a ON a.CompanyId = c.CompanyId AND a.RunId = c.CurrentRunId
     WHERE c.CurrentRunId IS NOT NULL AND (@CompanyId IS NULL OR c.CompanyId = @CompanyId)
       AND UPPER(LTRIM(RTRIM(a.UnitCode))) COLLATE Latin1_General_BIN2 IN (@UnitKey COLLATE Latin1_General_BIN2, @Bare COLLATE Latin1_General_BIN2)
       AND a.IsCancelled = 0 AND a.UnitCode NOT LIKE N'%*%' AND a.UnitId > 0;

    SELECT m.CompanyId, m.TenantId COLLATE DATABASE_DEFAULT AS TenantId, m.UnitId, m.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, m.OpenRows, m.SettledRows, CAST(0 AS bit) AS IsCancelled FROM #m m ORDER BY m.CompanyId, m.TenantId, m.UnitId;

    SELECT a.CompanyId, a.TenantId COLLATE DATABASE_DEFAULT AS TenantId, a.UnitId, a.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, a.OpenRows, a.SettledRows, a.IsCancelled
      FROM (SELECT DISTINCT CompanyId, RunId, TenantId FROM #m) k
      JOIN dbo.CollectionsRcvAccountUnit a ON a.CompanyId = k.CompanyId AND a.RunId = k.RunId AND a.TenantId = k.TenantId
     ORDER BY a.CompanyId, a.TenantId, a.UnitId;

    SELECT c.CompanyId, c.LastSuccessUtc FROM dbo.CollectionsRcvCompany c WHERE c.CurrentRunId IS NOT NULL AND (@CompanyId IS NULL OR c.CompanyId = @CompanyId);
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetTowers
AS
BEGIN
    SET NOCOUNT ON;
    -- The picker lists the active towers NEWEST FIRST: the tower number as an integer, descending (TP140 ... TP119), a non-numeric number after the numeric ones, then company and id.
    -- The name is the one every screen shows (V012 fn_Collections_TowerNames); a tower with no known name keeps its stored text and the application shows the number only.
    SELECT t.TowerId, LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) AS TowerNumber, COALESCE(n.TowerName, CONVERT(nvarchar(400), t.TowerName)) AS TowerName,
           t.CompanyId, CAST(t.IsActive AS bit) AS IsActive
      FROM dbo.CollectionsTowers t
      CROSS APPLY (SELECT LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) AS Num) k
      LEFT JOIN dbo.fn_Collections_TowerNames() n ON n.CompanyId = t.CompanyId AND n.TowerNumber = k.Num
     WHERE t.IsActive = 1 AND t.CompanyId IN (4, 32)
     ORDER BY CASE WHEN LEN(k.Num) BETWEEN 1 AND 9 AND k.Num NOT LIKE N'%[^0-9]%' THEN 0 ELSE 1 END,
              CASE WHEN LEN(k.Num) BETWEEN 1 AND 9 AND k.Num NOT LIKE N'%[^0-9]%' THEN CONVERT(int, k.Num) END DESC,
              CASE WHEN LEN(k.Num) BETWEEN 1 AND 9 AND k.Num NOT LIKE N'%[^0-9]%' THEN N'' ELSE k.Num COLLATE Latin1_General_BIN2 END,
              t.CompanyId, t.TowerId;
END;
GO
