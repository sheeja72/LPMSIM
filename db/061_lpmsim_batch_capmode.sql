-- =============================================================================
-- 061_lpmsim_batch_capmode.sql  (1.14.107)
-- -----------------------------------------------------------------------------
-- LPMSIM_Batch.CapMode — per-batch record of which cap formula the allocator
-- used. Two values today, more can be added later:
--
--   'EOM_BAL'  → cap = TargetEOM − DivSOH − cumDiv  (new default in 1.14.107)
--   'MNM'      → cap = MerchNeedMonth − cumDiv      (legacy / pre-1.14.107)
--
-- SKU Max remains the per-item cap in both modes; only the per-(Store, Div)
-- cap differs. See LpmSimGenerator.AllocateLineNormal.
--
-- Nullable so an inserted batch from older code (pre-1.14.107) doesn't fail
-- the INSERT — the C# allocator reads NULL as "legacy MNM behavior" for
-- back-compat. Existing rows get backfilled to 'MNM' here so reports can
-- surface a non-null label everywhere.
-- =============================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE Name = 'CapMode'
       AND Object_ID = Object_ID('LPMSIM.dbo.LPMSIM_Batch'))
BEGIN
    ALTER TABLE LPMSIM.dbo.LPMSIM_Batch
        ADD CapMode varchar(20) NULL;
END;
GO

-- Backfill: every batch that exists today (created pre-1.14.107) ran under
-- the MerchNeedMonth + SKU Max cap. Idempotent — only updates NULL rows.
UPDATE LPMSIM.dbo.LPMSIM_Batch
   SET CapMode = 'MNM'
 WHERE CapMode IS NULL;
GO

-- Optional sanity check (commented out — uncomment in SSMS if you want to verify):
-- SELECT CapMode, COUNT(*) AS BatchCount
--   FROM LPMSIM.dbo.LPMSIM_Batch
--  GROUP BY CapMode
--  ORDER BY CapMode;
