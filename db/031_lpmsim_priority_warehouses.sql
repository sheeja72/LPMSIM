-- ============================================================================
-- Migration 031 — Add PriorityWarehouses to LPMSIM_ProductionSchedule
--
-- Stores the comma-separated list of warehouse codes the planner flagged
-- as "priority" when generating the schedule. Boxes from those warehouses
-- get dispatched first within each (Day, Division) slot — ahead of
-- non-priority warehouses but after LPM-tagged boxes.
--
-- NULL or empty = no priority warehouses (all warehouses treated equal).
-- ============================================================================
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.LPMSIM_ProductionSchedule', 'PriorityWarehouses') IS NULL
BEGIN
    ALTER TABLE dbo.LPMSIM_ProductionSchedule
        ADD PriorityWarehouses varchar(500) NULL;
    PRINT 'LPMSIM_ProductionSchedule: added PriorityWarehouses';
END
ELSE
    PRINT 'LPMSIM_ProductionSchedule.PriorityWarehouses already exists';
GO

PRINT 'Migration 031 complete.';
