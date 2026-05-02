-- LPM SIM: realign Division.DivCode to match racks.subclassmaster.DivID by Division name.
-- After this migration: Accessories=401, Bags=402, … Womenswear=419, BFL Services=420, LFL=421.
-- Updates Division + every child table that uses DivCode (LPMDivMax, LPM_EOM_Output,
-- LPM_SalesTurns, LPM_Planned, LPM_WHStock, LPM_SKUMaxRule) atomically without dropping FKs.
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

DECLARE @Map TABLE (OldDiv int PRIMARY KEY, NewDiv int NOT NULL UNIQUE, DivisionName nvarchar(255));

INSERT INTO @Map (OldDiv, NewDiv, DivisionName)
SELECT d.DivCode, sm.DivID, d.Division
  FROM dbo.Division d
  INNER JOIN (SELECT DISTINCT DivID, Division FROM racks.dbo.subclassmaster WHERE DivID IS NOT NULL) sm
          ON LTRIM(RTRIM(sm.Division)) = LTRIM(RTRIM(d.Division));

-- Abort if any LPMSIM division cannot be mapped or if old/new ranges overlap.
DECLARE @missing  int = (SELECT COUNT(*) FROM dbo.Division d LEFT JOIN @Map m ON m.OldDiv = d.DivCode WHERE m.OldDiv IS NULL);
DECLARE @overlap  int = (SELECT COUNT(*) FROM @Map a INNER JOIN @Map b ON a.NewDiv = b.OldDiv);
IF @missing > 0 OR @overlap > 0
BEGIN
    PRINT 'Mapping incomplete: missing=' + CAST(@missing AS varchar) + ', overlap=' + CAST(@overlap AS varchar) + '. Aborting.';
    ROLLBACK TRANSACTION;
    RETURN;
END

-- Already realigned? (every Division.DivCode equals its target NewDiv.)
IF NOT EXISTS (SELECT 1 FROM @Map m WHERE m.OldDiv <> m.NewDiv)
BEGIN
    PRINT 'Division.DivCode is already aligned to subclassmaster.DivID. Nothing to do.';
    ROLLBACK TRANSACTION;
    RETURN;
END

-- Step 1 — Insert new Division rows (401..421) so child FKs resolve during the update.
INSERT INTO dbo.Division (DivCode, Division)
SELECT m.NewDiv, d.Division
  FROM dbo.Division d
  INNER JOIN @Map m ON m.OldDiv = d.DivCode
 WHERE NOT EXISTS (SELECT 1 FROM dbo.Division x WHERE x.DivCode = m.NewDiv);

PRINT 'Step 1: inserted shadow rows in Division.';

-- Step 2 — Repoint every child table at the new DivCode.
UPDATE c SET c.DivCode = m.NewDiv FROM dbo.LPMDivMax       c INNER JOIN @Map m ON m.OldDiv = c.DivCode;
PRINT 'Step 2a: LPMDivMax updated, rows=' + CAST(@@ROWCOUNT AS varchar);

UPDATE c SET c.DivCode = m.NewDiv FROM dbo.LPM_EOM_Output  c INNER JOIN @Map m ON m.OldDiv = c.DivCode;
PRINT 'Step 2b: LPM_EOM_Output updated, rows=' + CAST(@@ROWCOUNT AS varchar);

UPDATE c SET c.DivCode = m.NewDiv FROM dbo.LPM_SalesTurns  c INNER JOIN @Map m ON m.OldDiv = c.DivCode;
PRINT 'Step 2c: LPM_SalesTurns updated, rows=' + CAST(@@ROWCOUNT AS varchar);

UPDATE c SET c.DivCode = m.NewDiv FROM dbo.LPM_Planned     c INNER JOIN @Map m ON m.OldDiv = c.DivCode;
PRINT 'Step 2d: LPM_Planned updated, rows=' + CAST(@@ROWCOUNT AS varchar);

UPDATE c SET c.DivCode = m.NewDiv FROM dbo.LPM_WHStock     c INNER JOIN @Map m ON m.OldDiv = c.DivCode;
PRINT 'Step 2e: LPM_WHStock updated, rows=' + CAST(@@ROWCOUNT AS varchar);

UPDATE c SET c.DivCode = m.NewDiv FROM dbo.LPM_SKUMaxRule  c INNER JOIN @Map m ON m.OldDiv = c.DivCode;
PRINT 'Step 2f: LPM_SKUMaxRule updated, rows=' + CAST(@@ROWCOUNT AS varchar);

-- Step 3 — Drop the old Division rows (no children reference them now).
DELETE FROM dbo.Division WHERE DivCode IN (SELECT OldDiv FROM @Map);
PRINT 'Step 3: removed old Division rows, rows=' + CAST(@@ROWCOUNT AS varchar);

PRINT 'DivCode realignment complete.';
COMMIT TRANSACTION;
