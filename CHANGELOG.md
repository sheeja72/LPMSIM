# LpmSim — Release Notes

Versions follow [SemVer](https://semver.org/) on the deployable Web project (`src/LpmSim.Web/LpmSim.Web.csproj`):

- **MAJOR** — breaking schema/API changes that require operator action beyond running the listed migrations.
- **MINOR** — new features. Backward-compatible after running the listed migrations.
- **PATCH** — bug fixes / perf improvements only.

The version surfaces in the sidebar footer at runtime so operators can verify which build is in production.

## How to bump on each release

1. Edit `src/LpmSim.Web/LpmSim.Web.csproj`:
   ```xml
   <Version>X.Y.Z</Version>
   <AssemblyVersion>X.Y.Z.0</AssemblyVersion>
   <FileVersion>X.Y.Z.0</FileVersion>
   <InformationalVersion>X.Y.Z (Phase tag — short headline)</InformationalVersion>
   ```
2. Append a new section to this file under `## Unreleased` (move highlights into the new version's section).
3. Commit the bump as its own change (`chore: bump version to X.Y.Z`) so the release boundary is visible in `git log`.
4. Push.

---

## 1.9.4 — Fix #NewSnap persistence between SqlCommands (2026-05-11)

### Fixed
- **`Invalid object name '#NewSnap'`** in the exclusions phase of `BuildItemSkuMaxAsync`. The staging temp table was being created via `SELECT INTO` inside a `BEGIN TRY / BEGIN TRAN / COMMIT` block, and SQL Server in some configurations doesn't reliably keep the temp table accessible past the COMMIT for subsequent `SqlCommand` executions on the same connection. The 12-minute build succeeded at staging (15.7M rows) but the next command couldn't see `#NewSnap`, so all 7 override rules and the delta-apply phase silently skipped.
- Dropped the transaction wrapper around the `SELECT INTO #NewSnap` + `CREATE CLUSTERED INDEX` pair — matches the pattern used by the other temp tables in this method (`#ItemWh`, `#Stores`, `#Rules`, `#Deact`), which have always been created outside any transaction and persist correctly across subsequent commands. Atomicity is not compromised: `SELECT INTO` is itself a single statement, and if it fails SqlClient throws naturally.

---

## 1.9.3 — SKU Max delta-apply via staging snapshot (2026-05-11)

### Changed
- **`BuildItemSkuMaxAsync` no longer rewrites every row on every build.** Replaced the legacy `DELETE-all + INSERT-all` against `dbo.LPM_SimItemSkuMax` with a **staging snapshot + delta MERGE** pattern:
  1. **Staging:** the rule-computed `(Store × Item × Season) → SKUMax` snapshot is built in a tempdb `#NewSnap` table (clustered on `StoreID, ItemCode, Season` — no nonclustered indexes, so writes are ~4× cheaper than the production table).
  2. **Overrides on staging:** the 7 override rules (1-7 plus the per-rule TRY/CATCH from 1.9.2) compute matches against `#NewSnap` and `UPDATE` its `SKUMax` in place — temp-to-temp updates are fast.
  3. **Delta apply** to `dbo.LPM_SimItemSkuMax`:
     - **Drop** the 3 nonclustered indexes (`IX_…_Lookup`, `IX_…_Item`, `IX_…_Div`)
     - **DELETE** rows in target for the period not in `#NewSnap` (scope-aware for LpmOnly/NonLpmOnly)
     - **UPDATE** rows whose `(SKUMax, WHBoxQty, VolumeGroup, DivCode)` differ from `#NewSnap` (option **b**: skip only when all four match)
     - **INSERT** rows in `#NewSnap` not yet in target
     - **Recreate** the 3 nonclustered indexes
- **Expected speedup:** first build for a period drops from ~12 min to ~5 min (index maintenance avoided). Rebuilds with no changes drop to ~30s (staging built but only a few rows actually written). Rebuilds with 1-5% changes typically land in 1-2 min.
- **StageDetail format** extended:
  ```
  Done · X items · ... · Insert Yms · 15,704,550 staged · Delta Zms ·
  N ins · M upd · K del · J unchanged · 1,234 excluded · 567 price-capped · ...
  ```
- **Safety:** when the exclusions phase fails catastrophically (`exclusionWarning` non-null), the delta-apply is SKIPPED — the prior build's data in `dbo.LPM_SimItemSkuMax` is preserved untouched. The StageDetail surfaces `Delta SKIPPED (exclusions failed — target unchanged)` so the planner sees what happened.
- **No data-model change** — no migration needed.

### Notes
- During the ~30s-2min "indexes-dropped" window, any concurrent reads of `dbo.LPM_SimItemSkuMax` for OTHER `(Country, Year, Month)` periods will fall back to the clustered index (still works, but slower than seeking through the dropped NCIs). If you have many concurrent same-instance read workloads against this table across periods, schedule SKU Max builds during quiet windows.
- The build is still serialised per `(Country, Year, Month)` by `SkuMaxBuildJobManager` — no risk of concurrent same-period builds racing on the temp tables.

---

## 1.9.2 — SKU Max per-rule TRY/CATCH isolation (2026-05-10)

### Fixed
- **One failing override rule no longer silently kills the other six.** The 7 SKU Max override rules previously ran inside a single `BEGIN TRY / BEGIN TRAN ... END TRY / BEGIN CATCH` SQL block, so a single failure (Hodata access denied on Rule 5, schema drift on Rule 4's `usa.dbo.upcbarcodes`, missing linked server, etc.) rolled the entire batch back — `LPM_SimItemSkuMaxExcluded` ended up empty and SIM Generate behaved as if no overrides existed. Symptom: empty audit table after a successful-looking 12-minute build with no per-rule timings in the StageDetail.

### Changed
- **Three-phase structure** in `BuildItemSkuMaxAsync`'s exclusions block:
  - **Phase 1 (no transaction):** Snapshot + temp table setup. Errors here are still fatal — the rest of the phase is meaningless without `#SkuSnap`.
  - **Phase 2 (`SET XACT_ABORT OFF`):** Each rule's `INSERT INTO #*Matches` runs in its own `BEGIN TRY / END TRY / BEGIN CATCH / END CATCH`. Failures store `ERROR_MESSAGE()` in `@r1Error..@r7Error` and let the next rule run on the same `#SkuSnap`. INSERT is atomic per statement in SQL Server, so a failed rule leaves zero rows in its match table — no risk of half-applied state.
  - **Phase 3 (`SET XACT_ABORT ON`, transactional):** Audit + UPDATE wrapped in a single transaction. The `DELETE FROM LPM_SimItemSkuMaxExcluded` is INSIDE the transaction now so a failed apply doesn't wipe the prior audit rows.
- **Build banner** now reports per-rule failures alongside the timings, e.g.:
  ```
  X excluded · Y price-capped · K div-deact · L dept-deact
   [R1 ms · R2 ms · R3 ms · R4 ms · R5 ms · R6 ms · R7 ms]
   · R5 SKIPPED (Login failed for user 'svc_planning_hub' on Hodata) | R4 SKIPPED (...)
  ```
  Failed rules are surfaced after the timing breakdown with the SQL `ERROR_MESSAGE()` truncated to 80 chars per rule.

---

## 1.9.1 — Migration 040 constraint-name fix (2026-05-10)

### Fixed
- **Migration 040** — `dbo.LPM_StoreDeptAccess` failed with `Msg 2714 — There is already an object named 'DF_LSDA_IsActive'`. Constraint names use a per-schema unique namespace and the `LSDA` prefix was already taken by `dbo.LPM_StoreDivAccess` (migration 022). Renamed all five constraints in 040 to use the `LSDeptA` prefix (`DF_LSDeptA_DeptPct`, `DF_LSDeptA_IsActive`, `DF_LSDeptA_CreateTS`, `CK_LSDeptA_DeptPct`). The migration is idempotent (`IF OBJECT_ID(...) IS NULL`) so re-running it after the fix creates the table cleanly. PK + UQ names (`PK_LPM_StoreDeptAccess`, `UQ_LPM_StoreDeptAccess`) were already unique and unchanged.
- Misleading `Migration 040 complete.` message — the final `PRINT` is in a separate batch (after `GO`) so it printed even when the `CREATE TABLE` batch failed. The fix doesn't change this — re-running on a clean DB now produces both `Created dbo.LPM_StoreDeptAccess` AND `Migration 040 complete.`

### Migrations to apply (in order)
`040` (re-run; the prior failed attempt didn't actually create the table, so the `IF OBJECT_ID IS NULL` guard will retry cleanly).

---

## 1.9.0 — Weekly Sales Target Split + per-week Merch Need + SKU Max Rules 5 & 7 (2026-05-10)

### Added
- **Weekly Sales Target Split admin page** (`/lpm/weekly-sales-target-split`, role-gated to Admin / PlanningManager) under **Planning Config**. Per-(Country × Year × Month × Division × Week) split percentage of the monthly Target Sales across 4 logical weeks. Each row shows Wk1 / Wk2 / Wk3 / Wk4 plus a live Total cell that turns red unless the row sums to 100%. **Save** is disabled until balanced. **Copy prev month** carries forward only the *custom* rows from the previous period — default rows stay default. **Reset to default** per row deletes the saved split and falls the row back to 20 / 20 / 25 / 35.
- **New table `dbo.LPM_WeeklySalesTargetSplit`** (migration **038**) with `(Country, Year1, Month1, DivCode, WeekNo)` UNIQUE; CHECK on WeekNo (1..4) and SplitPct (0..100). Row-level audit on save / delete via the existing `IActionLogger`.
- **Per-week Merch Need columns on `LPM_EOM_Output`** (migration **039**) — `MerchNeedWeek1`, `MerchNeedWeek2`, `MerchNeedWeek3`, `MerchNeedWeek4`. Filled by `EomCalculator` at Approve time using the new formula:
  ```
  MerchNeedWeekN = (TargetEOM − SOH) / 4 + (TargetSales × SplitPct[N] / 100)   for N = 1..4
  ```
  Falls back to the hard-coded default split **20 / 20 / 25 / 35** when no row exists for `(Country, Year, Month, DivCode)`. Legacy `MerchNeedWeek` column now mirrors `MerchNeedWeek1` so existing readers (ADM, ProductionScheduler, the Reports queries, the Custom Report engine) keep working without a wider refactor.
- **`LPMSIM_Batch.WeekNo`** (migration **039**) — tinyint NULL stamped from the new Week dropdown on SIM Generate; pre-migration batches stay NULL and display as Week 1.
- **SIM Generate — Week dropdown** (1..4, required, default 1) next to Run Date. The allocator's per-Store × Div weekly cap now reads `MerchNeedWeek{N}` per the chosen week (via a `CASE @weekNo` on the `LPM_EOM_Output` SELECT), not the legacy `MerchNeedWeek` column. Same EOM run can be re-allocated for a different week without re-running EOM.
- **EOM Generate — 4 new columns Wk1 / Wk2 / Wk3 / Wk4** in the per-store result table, each with its column total in the header. Tooltips spell out the formula and the default split.

### Changed
- `EomCalculator.CalculateAsync` loads the active splits for `(Country, Year, Month)` once at the top, then drives the per-week computation in the existing SOH-merge loop. Rounded `AwayFromZero` (matches existing convention).

### Added (continued) — SKU Max Build
- **New SKU Max Rule 5** — `usa.dbo.DeptPriceMaxQty_MH4` price-band cap. Looks up `(shopname, DivCode, Department, PriceF ≤ price ≤ PriceT)` per `(Store × Item)` row in the SKU Max snapshot and **REPLACES** `SKUMax = maxqty`. Department resolved from `Datareporting.dbo.subclassmaster`; price from latest `Hodata.dbo.SalesPrice.SalesRate` per item for `CostCode='001'` (MAX(TrnDate) wins). Rule writes audit rows to `LPM_SimItemSkuMaxExcluded` with `SourceTable = 'usa.dbo.DeptPriceMaxQty_MH4'` so admins can see which items got capped and to what value. Skips items missing a price; skips bands that don't cover the item's price; deactivated `(Store, Div)` rows are protected (excluded from the rule's input snapshot).
- **All 6 SKU Max override rules now write audit rows to `LPM_SimItemSkuMaxExcluded`** so admins can audit every override decision. Rule 6 (deactivation via `dbo.LPM_StoreDivAccess`) was previously applied silently at the main `INSERT`'s `CASE` clause — now it runs as a proper rule inside the exclusions transaction with `SourceTable = 'dbo.LPM_StoreDivAccess'` and a `PriorSKUMax` value (the rule-computed SKUMax that would have applied without the deactivation). The Pre-Generate deactivation sync (which catches deactivations made post-build) also writes audit rows now, with a distinct `Reason = 'Store-Div deactivated post-build (Pre-Generate sync)'`. The sync is idempotent — re-running it doesn't create duplicate audit rows.
- **New SKU Max Rule 7** — `dbo.LPM_StoreDeptAccess` (Store × Department deactivation). Finer-grained version of `LPM_StoreDivAccess` (which works at Division level). When a `(Country, Store, Div, Department)` tuple has `IsActive = 0`, every item in that Department under that (Store, Div) has its `SKUMax = 0`. Department resolved from `Datareporting.dbo.subclassmaster.Department` (same convention as Rule 5). Audit rows written with `SourceTable = 'dbo.LPM_StoreDeptAccess'`. The new `DeptPct` column on this table is reserved for a future EOM/SIM scaling rule and is currently NOT applied by the SKU Max build.
- **New admin page `/admin/store-dept-access`** under "Planning Config" (Admin / PlanningManager). Same UX as the existing Store / Division Access page — list / filter by country, deactivate / reactivate / edit / delete a (Store × Div × Department) row inline; multi-select Add dialog (Stores × DivDept pairs) with Active flag, DeptPct (default 100), and free-text Remarks. Audit-logged via `IActionLogger`.
- Build banner gains per-rule timings for Rules 5, 6, and 7 (`R5 Xms · R6 Yms · R7 Zms`) and the StageDetail now reports `N excluded · M price-capped · K div-deact · L dept-deact` with a per-rule timing breakdown.

### Fixed — SKU Max Build
- **`Invalid column name 'Shopname'` on Rule 2** (`usa.dbo.ExcludeSubclass`). The exclusion batch is wrapped in a single transaction, so Rule 2's failure rolled BACK rules 1, 3, 4 too — `LPM_SimItemSkuMaxExcluded` stayed empty across builds. Verified column on `ExcludeSubclass` is `Shop` (no `name` suffix); the join now references `es.Shop`. The other 3 tables (`ExcludeExport_Planning`, `RemoveItemsFromTransfer`, `ExcludeItemsMFCS`) use `ShopName` / `shopname` / `Shopname` respectively — all match SQL Server's case-insensitive comparison and were never broken.

### Migrations to apply (in order)
`038`, `039`, `040`.

### Notes
- After deploying, re-run **EOM Generate** for the active period so the new `MerchNeedWeek1..4` columns get populated. Until then they're NULL and the SIM allocator's `ISNULL(... , 0)` guards keep the run safe (cap of 0).
- Re-run **Build SKU Max** for the active period to pick up the Rule 5 price caps and the now-working Rules 1–4. The build banner's StageDetail will show `N excluded · M price-capped` instead of `exclusions SKIPPED (...)`.
- Tier 2 (making Reports / ADM / ProductionScheduler week-aware) is still pending — those readers continue to consume the legacy `MerchNeedWeek` column (= `Wk1` after re-Generate).

---

## 1.8.2 — Planning Hub rebrand + WH Boxes ContNo/LPM filters (2026-05-10)

### Changed
- **App rebrand: "LPM SIM" → "Planning Hub"** (UI labels only — no backend, DB, namespace, or route changes). Updated in 5 places: sidebar drawer brand title, sidebar drawer footer, home page H1 + `<PageTitle>`, Signed-out page text + reopen link, User Access page subtitle. Home page subtitle reworded from "Internal LPM simulator & data entry." to "Internal stock-allocation & merch-planning workspace." to reflect the broader scope.
- **Home page tiles redesigned** — removed `Division Max Entry` (superseded), `User Access` and `Audit Log` (admin-only, still reachable via side nav). Added three daily-workflow tiles: **EOM Generate**, **LPM SIM Generate**, **Warehouse Boxes** — visible to every signed-in user.
- **Side nav reorganized** — `Monthly Weights`, `Planned Inputs`, `Data Uploads` moved from "LPM Variables" section into "Planning Config". The "Planning Config" section header now sits outside the `<AuthorizeView Roles="Admin,PlanningManager">` so the three operational items remain visible to all signed-in users; the master-data pages (Store Grades, Volume Groups, SKU Max Rules, Store/Division Access, Warehouse Priorities) stay gated behind the Admin/PlanningManager roles.

### Added
- **Warehouse Boxes — ContNo column + filter.** New `ContNo` field on `WhBoxRow` and `WhBoxFilter`; `MAX(w.ContNo)` added to `GetBoxesAsync` SQL with the matching `(@contNo IS NULL OR w.ContNo = @contNo)` predicate; same `@contNo` filter wired into the rolled-up summary queries for consistency. New `WarehouseQueryService.GetContNosAsync()`. UI gets a new ContNo autocomplete dropdown on the filter row; ContNo column added to the Box-detail table and Excel export.
- **Warehouse Boxes — LPM month dropdown.** A `MudAutocomplete` populated from `WarehouseQueryService.GetLpmsAsync()` (already existed in the service; was previously not exposed on the UI). Distinct from the existing **LPM Status** select (Any / Has / No) — this one filters to a specific LPM month value (e.g. `May-26`).

### UI / Layout
- **Box-detail column order** — `ContNo` and `LPM` moved to the front of the row (after Warehouse) so each line reads "container → pallet → box". On screen and in Excel, the new order is: `Country | Warehouse | ContNo | LPM | Pallet No | Box No | Pallet Type | Type Name | Pallet Category | Division | Department | Brand | Rack | Purchased | Qty`.

---

## 1.8.1 — Allocation Result × Warehouse + WH Box filter rename (2026-05-10)

### Added
- **Allocation Result table redesigned** on the SIM Generate result preview. Old shape (LPM/Non-LPM × Lines/Stores/Boxes/Qty) replaced with one row per `(Kind × Warehouse)` showing **SKU Count** (distinct itemcodes), **Boxes** (distinct), **Box Qty** (warehouse-side stock), and **Output SIM Qty** (allocated). New `SourceWarehouseRow` record + `GetSourceWarehouseBreakdownAsync` query (pre-aggregated CTEs against `racks.dbo.whboxitems` — no per-row subqueries).

### Changed
- **Warehouse Boxes page — checkbox renamed and inverted.** "Show Non-Purchased" (default ON, applied the `<> 'E'` filter) → **"Include non-Purchased Boxes"** (default OFF, drops the `<> 'E'` filter when ticked). Same semantics as the existing SIM Generate "Include Non-Purchased Boxes" flag. Field renamed `WhBoxFilter.NonPurchasedOnly` → `IncludeNonPurchased`. Default behaviour unchanged.

### Fixed
- **Warehouse Boxes header alignment.** Totals in the `.lpm-th-total` spans were drifting above adjacent column headers when the row-count cell (single-line content) was shorter than its multi-line neighbours. Added `lpm-wh-table` class to all four MudTables on the page; CSS forces `vertical-align: top` and `nowrap + ellipsis` on totals so every header column lines up cleanly.

---

## 1.8.0 — Phase H — Custom Report + Warehouse Priorities + ADM (2026-05-09)

### Added
- **Custom Report tab** on SIM Generate — multi-select `Group By` + `Columns`, dynamic SQL builder via field whitelist (injection-safe), Excel export.
- **Warehouse Priorities** admin page (`/admin/warehouse-priorities`) backed by new `dbo.LPM_WarehousePriority` table (migration **037**). Lower number = higher priority; missing rows fall to 9999 (sort last). Box read-order in `ReadBoxesAsync` joins to this table.
- **ADM (Allocation Distribution Model)** page — separate menu, new `LPMSIM_ADM_Run` + `LPMSIM_ADM_BoxAlloc` tables (migration **033**).
- **Production Schedule** page — new `LPMSIM_ProductionSchedule` table (migration **029**).
- **Background SKU Max build** (`SkuMaxBuildJobManager`) with three scopes (All / LPM-only / Non-LPM-only). Per-scope DELETE preserves rows for items in the other scope. Cancel button + force-close on cancel for snappy abort. Build-history persisted in `LPM_SimItemSkuMaxBuild` (migration **032**).
- **SKU Max exclusion rules** — four source tables (`usa.dbo.ExcludeExport_Planning`, `ExcludeSubclass`, `bfldata.dbo.RemoveItemsFromTransfer`, `usa.dbo.ExcludeItemsMFCS`). Run as a separate batch so a schema mismatch on any source is non-fatal — the snapshot rebuild still commits with `exclusions SKIPPED` in the StageDetail (migration **034**).
- **Pre-Generate sync of `LPM_StoreDivAccess` deactivations** into the SKU Max snapshot — deactivation changes take effect on next Generate without a SKU Max rebuild.
- **`PriorityRank` (Store × Div)** column on Summary, Item Details, and Custom Report — surfaces the rank that drives `EqualPerStore` (primary) and `EqualFillRate` (tiebreak) sort.
- **EOM Balance ≤ 0 gate** in allocator — over-stocked `(Store, Div)` pairs are skipped with `SKIP_EOM_BALANCE` trace tag.
- **`MultiSelectFilter` reusable component** — search box + checkbox list in a popover; replaces substring-matching `MudAutocomplete` filters that incorrectly matched "Menswear" inside "Womenswear".
- **Item Details** UI: `BoxNo` removed, `Qty → BoxQty`, new `LPM` chip column, `LPM Qty → SIM Qty`, **Rank** column.
- Multiple-batches-per-period support — drop `UQ_LPMSIM_Batch_CountryRunDate` (migration **036**), Generate keeps Approved batches and stamps a new Draft alongside.
- Pallet Categories multi-select on SIM Generate (replaces the "Include All LPM Boxes" checkbox).
- LPM Months multi-select on SIM Generate.
- Warehouse Boxes report enhancements (Division / Department / Brand / Rack / Purchased columns, group-by modes, Excel export).

### Changed
- **Default `FillStrategy` = Ranking + RR** (was Equal fill %). The Equal fill % radio still selectable.
- **Item Details query rewritten** — pre-aggregated CTEs (`BoxAttrs`, `BoxTotals`) replace per-row correlated subqueries against `racks.dbo.whboxitems`. Multi-second → sub-second on a 116K-row batch.
- **`SKUMax` column on Item Details** now reads from `LPM_SimItemSkuMax` (per-Store × Item) rather than `LPM_EOM_Output.SKUMax` (per-Store × Div) — the column total no longer reads as 0 when EOM rows are missing.
- **`ItemDiv` resolution on Item Details** uses LocStock-first / upc_subclass fallback (matches the allocator). Pre-fix the report was upc_subclass-only and silently dropped lines for items not in upc_subclass.
- **`FillStrategy` widened** to `varchar(40)` (migration **035**) so the "(SKU Max only)" tag fits.
- Engine column-name comment + audit `Reason` strings updated for `usa.dbo` schema's `Shopname` convention.
- StoreID prefix added to every Store cell — fixes the "blank stores" rendering when `DataSettings.PBFullname` is null.
- Multi-select with checkboxes (search + tick) on Filter Store / Filter Division — exact match, not substring.

### Migrations to apply (in order)
`024`, `025`, `026`, `027`, `028`, `029`, `030`, `031`, `032`, `033`, `034`, `035`, `036`, `037`.

---

## 1.7.0 — Phase G — SQL-side SKU Max build (initial import, 2026-04-22)

Initial baseline imported into the new repo. Phase G moved the per-period SKU Max build entirely into a SQL `INSERT … SELECT` driven by temp tables — about 5–10× faster than the previous C# bulk-copy round-trip.
