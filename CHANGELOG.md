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

## 1.14.19 — SKU Max Rule 1 (ExcludeExport_Planning): Active + Duration + GroupCode (2026-05-14)

### Rule 1 (`usa.dbo.ExcludeExport_Planning`) — new business logic
Pre-1.14.19 the rule was a plain `Shopname × ItemCode` join: every row in `ExcludeExport_Planning` matched against every snapshot row regardless of status or scope. 1.14.19 layers four business filters on top:

1. **`Active = 'Y'` required** — `Active = 'N'` (or NULL/blank) rows are ignored. Case-insensitive.
2. **Duration handling** — if `Duration = 'Temporary'` (case-insensitive), `CAST(GETDATE() AS date)` must fall between `BlockFrom` and `BlockTo` inclusive. Anything else (Permanent, empty, NULL) skips the date check.
   - Date-only compare so a `smalldatetime` value with a time-of-day component doesn't accidentally drop the boundary day.
   - NULL `BlockFrom` or NULL `BlockTo` on a Temporary row ⇒ `BETWEEN` is UNKNOWN ⇒ block does **not** apply (fail-safe).
3. **`ItemCode` non-empty ⇒ item-level block (rule 1a)** — same `Shopname × ItemCode` join as before, just gated by the new filters.
4. **`GroupCode` non-empty ⇒ group-level block (rule 1b)** — resolves group membership via `Hodata.dbo.ItemMaster.GroupCode = ep.GroupCode AND im.ItemCode = snap.ItemCode`. So every item in the group gets blocked for that Shopname.

Business convention is `ItemCode XOR GroupCode` per row — never both. 1b is gated on `ItemCode` being empty so if a malformed row ever has both, the item-level rule (1a) wins (more specific).

### Implementation
- Single `INSERT INTO #ExcludeMatches` with a `;WITH ActiveExcludes AS (...)` CTE common-filter + `UNION ALL` for 1a and 1b. Keeps both branches in the same TRY block — partial failure semantics unchanged (whole rule fails as one if it throws).
- `MatchedKey` column now carries `'GroupCode=<x>'` for 1b matches so the trace report can tell item-level from group-level blocks at a glance. 1a stays NULL (matched on ItemCode, no extra info needed).
- `Reason` column distinguishes "Active item-level block" vs "Active group-level block".

### Behaviour change to watch
- More rows might be blocked (group-level matches now activate that previously did nothing — the table likely had GroupCode rows before this release that weren't applied).
- Fewer rows might be blocked (`Active = 'N'` rows and expired Temporary blocks were previously included; now they aren't).
- Net direction depends on what's in the table; impossible to predict without running the build.

### Notes
- No DB migration. Pure SQL-text change inside `BuildSkuMaxAsync`.
- Rules 2-7 (`ExcludeSubclass`, `RemoveItemsFromTransfer`, `ExcludeItemsMFCS`, `DeptPriceMaxQty_MH4`, `LPM_StoreDivAccess`, `LPM_StoreDeptAccess`) unchanged.

---

## 1.14.18 — LPMSIM_Output extra columns + box/item Season split + SKU Max archive (2026-05-14)

### Added — 6 new columns on `dbo.LPMSIM_Output` (+ backup table)
- **`Season`** `char(1) NULL` — `'S'` or `'W'` from `whboxitems.Season` (per-item, so a mixed pallet can hold both).
- **`BoxQty`** `bigint NULL` — total qty in the source box (`SUM(whboxitems.Qty) PARTITION BY BoxNo`).
- **`BoxItemQty`** `int NULL` — source qty of THIS item in the box (distinct from `LPMSIM_Output.Qty`, which is the **allocated** qty to a store).
- **`UsabilityPct`** `decimal(5,2) NULL` — per-box: `SUM(LPMSIM_Output.Qty for this BoxNo) / BoxQty × 100`, rounded to 2 dp.
- **`DivCode`** `int NULL` — item's division (`upc_subclass × subclassmaster × Division`).
- **`SKUMax`** `int NULL` — the SKU Max value the allocator used for this (Store, Item) at allocation time.

### Box-level vs item-level Season — undoing the 1.14.17 Season parts
After 1.14.17 dropped the `pallettype` JOIN entirely, both **box selection** and **item filtering** went through `w.Season`. 1.14.18 splits the two concerns:

| Level | Source | What it does |
|---|---|---|
| Box selection (SQL `seasonClause`) | `pt.Season` (pallettype master) | User picks Summer ⇒ only Summer-tagged **pallets** are returned by `ReadBoxesAsync`. |
| Item allocation (in-memory filter) | `w.Season` (whboxitems per row) | After read, items whose own `w.Season` doesn't match the user's choice are dropped via `lpmBoxes/nonLpmBoxes.RemoveAll`. A Summer pallet that carries a few Winter items will see those Winter items skipped during allocation. |
| Input Readiness grid | `pt.Season` | Reverted to box-level counts (matches the box-selection logic the user actually picks against). |
| `PalletCategory` filter | `w.PalletCategory` | Unchanged from 1.14.17 — category data on `w.*` is identical to `pt.*` and the JOIN is unnecessary for this. |

This restores the original 1.14.16-and-earlier box-level Season behaviour while adding the per-item drop the user asked for. Effect: **boxes containing mixed Summer/Winter items now contribute only their season-matching items to allocation**, rather than dropping the whole box (1.14.17 behaviour) or allocating the wrong-season items (pre-1.14.17 behaviour, since `pt.Season` for the box wouldn't catch them).

### `LPM_SimItemSkuMax` archive-and-purge (migration 045)
- New table **`dbo.LPM_SimItemSkuMax_Backup`** mirrors `LPM_SimItemSkuMax` + `(BackupTS, BackupBy)`. PK includes `BackupTS` so re-archiving the same period multiple times is non-conflicting.
- `BuildSkuMaxAsync` now archives all **strictly-older** periods (same Country) into the backup at the end of each successful build, then deletes them from the main table. Production `LPM_SimItemSkuMax` therefore holds **only the latest period per country** going forward — much smaller, faster reads.
- Archive runs inside its own try/catch so a missing backup table (migration 045 not applied yet) or transient failure doesn't break the build — the snapshot for the just-built period is already committed; archive retries on the next build.

### Migration 044 — output columns + UAE backfill
- Idempotent `ALTER TABLE` on both `LPMSIM_Output` and `LPMSIM_Output_Backup` for all 6 new columns.
- Inline backfill for UAE batches via JOIN to `racks.dbo.whboxitems` for `Season` / `BoxItemQty` / `BoxQty`; `DivCode` via the standard `upc_subclass × subclassmaster × Division` chain; `SKUMax` from `LPM_SimItemSkuMax` with `MAX(SKUMax) GROUP BY (StoreID, ItemCode)` for defensive deduplication.
- `UsabilityPct` backfill runs last so `BoxQty` is already populated.
- Each backfill is guarded by `WHERE <col> IS NULL` so re-runs are no-ops.
- Non-UAE backfill stays as a documented manual block at the bottom for the operator to run per country once `DataName` is confirmed in `bfldata.dbo.DataSettings`.

### Migration 045 — `LPM_SimItemSkuMax_Backup`
- Idempotent `CREATE TABLE`. No data motion in the migration itself — the first Build SKU Max after deploy populates the backup naturally.

### Allocator changes (`LpmSimGenerator.cs`)
- `BoxItem` record extended with `BoxQty`; `ReadBoxesAsync` reader now consumes the `BoxQty` window column.
- After `ReadBoxesAsync` returns, items whose `w.Season` doesn't match the user's choice are removed from `lpmBoxes`/`nonLpmBoxes` (the per-item drop). Single point of truth, applies across all phases (P1a, P1b RR, P2a, P2b RR).
- Both Phase 1 and Phase 2 `new LpmSimOutput { ... }` constructions populate `Season`, `BoxQty`, `BoxItemQty`, `DivCode`, `SKUMax` from per-row state + existing lookups.
- A pre-bulk-insert pass stamps `UsabilityPct` on every output row using a `Dictionary<BoxNo, totalAllocated>` aggregate.
- `BulkInsertOutputAsync` DataTable + row population extended for the 6 columns.
- Backup INSERT (in `BackupAndDeleteAsync`) column list extended.

### Notes
- All columns are NULL-able and additive — existing batches stay valid pre-migration.
- **Must apply migrations 044 + 045 to prod DB** before or alongside the deploy. Without 044 the allocator's bulk insert throws on unknown columns; without 045 the archive step silently skips (build still succeeds).
- 1.14.17 is partially superseded — the box-level Season parts are reverted; the `PalletCategory` cleanup is kept.

---

## 1.14.17 — SIM Generate: drop pallettype JOIN, use w.Season + w.PalletCategory (2026-05-14)

### Performance + correctness
- **Input Readiness query** (the LPM/Non-LPM × Summer/Winter eligibility grid on the SIM Generate page) — dropped the `INNER JOIN bfldata.dbo.pallettype pt`. Season now read from `w.Season` (whboxitems / WHBoxItemsExport); PalletCategory filter now reads `w.PalletCategory`.
- **SIM Generate box enumeration** (`ReadBoxesAsync`) — same change. The per-row Season already represents item-level seasonality, so a mixed box (Summer-marked box containing some Winter items) gets each item tagged with its own Season exactly as the planner asked.
- **Input Freshness "last WH Box load" timestamp** — same change.
- **`BuildPalletCategoryClause`** helper now emits `AND w.PalletCategory IN (...)` (was `pt.PalletCategory`). Two callers updated.
- **`seasonClause`** now uses `UPPER(ISNULL(w.Season, ''))` — was `pt.Season`, would have errored at runtime after the JOIN drop without this fix.

### Why
Same rationale as 1.14.9's SKU Max Build switch (documented in `LpmSimGenerator.cs:2400`):
1. `INNER JOIN bfldata.dbo.pallettype pt` silently dropped boxes whose `PalletType` had no master row (latent data-integrity hole — those boxes vanished from the counts).
2. `pt.Season` / `pt.PalletCategory` could differ from `w.Season` / `w.PalletCategory` when the master was stale (e.g. box re-tagged in whboxitems but pallettype not updated).
3. WH Stock Position, Variance Report, EOM Calculator, and SKU Max Build all moved off `pt.*` in 1.13.2–1.14.9. SIM Generate is now consistent with the rest.
4. Removes a multi-million-row hash/merge JOIN per call — expected sub-second speedup on the Input Readiness panel and a meaningful chunk off SIM Generate's box-enumeration step.

### Behaviour change
- Boxes with no row in `bfldata.dbo.pallettype` (rare; usually a data-load timing artefact) are no longer silently excluded. They will now appear with their `w.Season` / `w.PalletCategory` as-is. Same trade-off 1.14.9 accepted.
- `UPPER()` added around `w.Season` matches makes the filter case-insensitive (`'w'` and `'W'` both treated as Winter), matching the 1.14.9 convention.

### Notes
- No DB migration. No schema change. No UI change.
- Reports (`WhHoStockService`, `VarianceReportService`, `LpmSimReports`) were already on `w.Season` / `w.PalletCategory` — not touched.

---

## 1.14.16 — Rename Viewer role label to Reports (2026-05-14)

### Role display labels renamed
- **Viewer** → **Reports** on the Admin → Users page (grid chip + Edit User dialog checkbox).
- `RoleCode` stays `'Viewer'` — every `[Authorize(Roles = Roles.Viewer)]` and every existing `LpmUserRole.RoleCode = 'Viewer'` row keeps working untouched.
- Migration **043** updates `dbo.LPMRole.RoleName` from `'Viewer'` to `'Reports'`. Idempotent. **Must be applied to prod DB** so the renamed label appears.

### Notes
- Sidebar role badge (`MainLayout.razor`) was intentionally **not** changed — it uses a separate C# `IsInRole(...)` map and still shows "Viewer" for users in the Viewer role. Flag if you want that flipped too.
- No code change. UI auto-picks up the new label because both display spots already read `RoleName` from `dbo.LPMRole`.

---

## 1.14.15 — Production Schedule perf: parallel detail queries (2026-05-14)

### Performance
- **Production Schedule load** sped up by firing the 3 detail-tab queries (Day summary, Box detail, Division summary) **in parallel** via `Task.WhenAll` instead of sequentially. Total wait is now `max(query)` instead of `sum-of-each`.
- Safe because `ProductionScheduler` is registered with `IDbContextFactory<LpmDbContext>` — each `Get*` method creates its own `DbContext` and SQL connection inside its `await using`, so concurrent execution doesn't share state. Each call still runs under `READ UNCOMMITTED` and is wrapped in the existing `WithDeadlockRetry` helper per call.

### Notes
- Behaviour-equivalent change. No schema migration. No UI change.
- Variant filter handler (`OnDivFilterChanged`) still runs Division Summary on its own — only the initial 3-tab load is parallelized, since filter changes only refresh the one tab.

---

## 1.14.14 — EOM/SIM nav group + role rename + Store/Div/Dept Access filters (2026-05-14)

### Sidebar reorganized
- New **EOM/SIM** collapsible MudNavGroup at the top (replaces the flat "LPM Variables" section header). Contains: EOM Generate, LPM SIM Generate, Production Schedule, ADM (Allocation), LPM SIM Reports.
- **Warehouse Boxes** moved from the top-level into the existing **Reports** group (now: WH Stock Position, Variance Report, Warehouse Boxes).
- "Planning Config" and "Admin" sections unchanged.

### Role display labels renamed
- **Editor** → **EOM/SIM** (matches the new menu group name)
- **PlanningManager** → **Planning Config** (matches the section name)
- Migration **042** updates `dbo.LPMRole.RoleName` for both codes. **`RoleCode` is unchanged** — every `[Authorize(Roles = Roles.Editor)]` and `[Authorize(Roles = Roles.PlanningManager)]` keeps working untouched. Existing `LpmUserRole` rows continue to bind via `RoleCode`.
- UI updates (display-only):
  - `Users.razor` chip now shows `RoleName` (with `RoleCode` fallback) via a new in-memory lookup.
  - `UserEditDialog.razor` checkbox labels show `RoleName` instead of `RoleCode`.
  - `MainLayout.razor` sidebar role badge maps `IsInRole("Editor")` → "EOM/SIM" and `IsInRole("PlanningManager")` → "Planning Config".

### New filters on Store / Division Access and Store / Department Access pages
- **Store / Division Access**: added Store + Division multi-select filters with type-ahead search (reuses the existing `MultiSelectFilter` component from Warehouse Boxes / WH Stock Position).
- **Store / Department Access**: added Store + Division + Department multi-select filters.
- Both default to empty (= no filter). Option lists are derived from the currently-loaded rows so the dropdowns stay in sync with what's displayable.

### Searchable Division / Department dropdowns
- **`Admin → SKU Max Rules`** — Division dropdown converted from `MudSelect` to `MudAutocomplete` (type-ahead, retains the "All divisions" option as the empty value).
- **`SIM Generate → Division Summary tab`** — multi-Division filter converted from `MudSelect MultiSelection=true` to `MultiSelectFilter` (gives type-ahead + Select-all button).
- The rest of the app's Division / Department pickers were already on `MudAutocomplete` or `MultiSelectFilter` — no change needed.

### Notes
- **Migration 042 must be applied to prod DB before deploy** so the renamed role labels appear immediately. If not applied, the UI falls back to showing `RoleCode` (still functional, just doesn't say "EOM/SIM" yet).
- No authorization model change. Permissions stay 1:1 with previous releases.
- No schema migration beyond the `RoleName` UPDATE.

---

## 1.14.13 — Sidebar version badge readability on yellow (2026-05-14)

### Fixed
- **`v1.14.x` badge next to "Planning Hub"** was light-gray text on a near-transparent white chip — readable on the old blue sidebar but invisible on the 1.14.5+ yellow sidebar. Flipped to **bold near-black (`#0F172A`) on a subtle dark chip** so the version label pops on every sidebar palette.
- **Drawer footer** ("Planning Hub · v1.14.x" at the bottom) recoloured from light-slate to slate-800 (`#1f2937`) with a dark divider, matching the new yellow contrast.

### Notes
- Theme colors only. No behaviour change.
- Other sidebar text (nav links, brand title) was already readable from earlier 1.14.5/1.14.7 changes.

---

## 1.14.12 — WH Stock + Variance perf + PalletNo on SIM Output (2026-05-13)

### Performance
- **WH Stock Position and Variance Report queries sped up** by materializing the shared `ItemDiv` and `ItemSeason` CTEs into indexed temp tables instead of inline CTEs. Same pattern as 1.14.10's Rule 5 fix.
  - `#WhRptItemDiv` / `#WhRptItemSeason` — temp tables in `WhHoStockService.GetAsync`
  - `#VarRptItemDiv` / `#VarRptItemSeason` — temp tables in `VarianceReportService.GetAsync`
  - Each gets a clustered index on `itemcode` so the downstream `HOByDiv` / `WHByDiv` / `HOByItem` / `WHByItem` joins are simple index seeks instead of CTE expansions over the full `usa.dbo.upcbarcodes` (18M rows) and `upc_subclass × subclassmaster` join.
- Expected 2-5× speedup on these two reports, especially on the WH side where the previous CTE expansion was joined into the 15.7M-row `whboxitems`.

### Added — PalletNo throughout SIM Output
- **Migration 041** adds a `PalletNo varchar(50) NULL` column to `dbo.LPMSIM_Output` and its backup table. Idempotent (`COL_LENGTH ... IS NULL` guard). Existing rows stay NULL; new allocations populate from `whboxitems.PalletNo`.
- **SIM allocator** (`LpmSimGenerator.BuildAsync` + `BulkInsertOutputAsync`) now reads `w.PalletNo` from the source box query, carries it through the `BoxItem` record and the two `LpmSimOutput` creation sites (Phase 1 + Phase 2 RR), and writes it to `LPMSIM_Output.PalletNo` via the bulk-copy DataTable. Allocation logic itself unchanged.
- **`LpmSimOutput` entity** + **`LpmSimOutput_Backup` INSERT** updated for the new column.
- **SIM Boxes tab** on SIM Generate page: new "Pallet No" column shown right after "Box No". Sortable, included in Excel export.
- **Item Details tab** on SIM Generate page: new "Pallet No" column shown right after "Item". Sortable, included in Excel export.
- **Report data layer** (`LpmSimReports.GetBoxDetailsAsync` + `GetItemDetailsAsync`) surfaces `PalletNo` from `whboxitems` via JOIN, so historical batches (where the persisted column is NULL on `LPMSIM_Output`) still display PalletNo on the report views.

### Notes
- No data-semantics change for any existing column. Output rows and metric totals are unchanged.
- Existing historical batches retain `NULL` on `LPMSIM_Output.PalletNo` until re-Approved.
- Reports JOIN to `whboxitems` regardless, so the UI shows PalletNo for all batches.
- `SET NOCOUNT ON` added at the top of the WH Stock and Variance batches so row-count messages from temp-table inserts don't surface in the SqlDataReader.
- EOM Generate uses a separate `EomCalculator.GetDivisionStockBreakdownAsync` query, untouched here.

---

## 1.14.11 — SIM Generate eligibility: all LPM months + LPM Non-Purchased column (2026-05-13)

### Changed
- **LPM column on the eligibility view now shows ALL LPM months** when "LPM Months = (All months)" is selected. Previously it filtered to `LPMDt < first-day-of-next-month` (current + elapsed only), so future-dated LPM tags (e.g. June 2026 boxes during a May 2026 run) were invisible. Specific-month selections still respect the filter as before.
- **New "LPM Non-Purchased" column** on the eligibility table — displays LPM boxes where `ShopEligible = 'E'` (still in-process / not yet purchased). The existing "LPM" column now means "LPM AND Purchased". Total column now sums LPM + LPM-NP + Non-LPM for the row.
- **Caption updated** to reflect the new buckets: *"LPM: LPMDt is set (all months — past, current, and future) · Non-LPM: LPMDt IS NULL · Non-Purchased: ShopEligible = 'E' (still in-process)"*.

### Internal
- `BoxSegmentCounts` record gained 8 new fields (LPM × Summer/Winter × Boxes/Qty, and Non-LPM × Summer/Winter × Boxes/Qty for the Non-Purchased buckets). Non-LPM-Non-Purchased fields exist for the `selBoxes`/`selQty` computation only — not displayed (per user spec).
- SQL refactored: dropped the WHERE-level `ShopEligible` filter (now in CASE statements), dropped the LPMDt date filter when `lpmMonths` is empty.
- `CheckAsync`'s "Eligible Boxes (Selected)" metric card preserves its old semantics — with "Incl. Non-Purchased" checked, the Non-Purchased buckets are added to the selected total; unchecked, only Purchased.

### Notes
- No schema change. No migration.
- The Non-LPM column on the table is unchanged behaviourally (still shows Purchased only).
- Other reports + SIM allocation untouched.

---

## 1.14.10 — SKU Max Rule 5: materialize lookups, single-pass SalesPrice (2026-05-13)

### Performance
- **Rule 5 (`DeptPriceMaxQty_MH4` price-band cap) sped up from ~14 minutes to an expected 1–3 minutes** by pre-aggregating the heavy lookups into indexed temp tables before the main join:
  - `#SkuItemsR5` — distinct itemcodes in `#SkuSnap` (the universe Rule 5 touches).
  - `#ItemAttrR5` — `(DivCode, Department)` per itemcode from `upc_subclass × subclassmaster × Division`, filtered to `#SkuItemsR5`.
  - `#ItemPriceR5` — latest `SalesRate` per itemcode from `Hodata.dbo.SalesPrice` using a **single `ROW_NUMBER()` pass** (was two passes — `LatestPriceDt` MAX + `ItemPrice` rejoin). Also filtered to `#SkuItemsR5` so the SalesPrice scan only touches relevant rows.
  - All three temp tables get a clustered index on `ItemCode`.
- **Main rule body unchanged behaviour-wise** — same `CROSS APPLY` to `usa.dbo.DeptPriceMaxQty_MH4` with the same join keys (DivCode, Department, Price band) and `snap.Shopname` from the 1.14.8 bridge.

### Notes
- No data semantics change. Same `#PriceCapMatches` content; just faster to compute.
- Other rules untouched. Rule 7 (LPM_StoreDeptAccess, ~2 min for 617K rows) and the 18-min Insert phase are separate concerns — if they're still too slow after this lands, they're the next targets.
- No schema change.

---

## 1.14.9 — SKU Max Build: read Season from whboxitems direct (2026-05-13)

### Changed
- **`BuildItemSkuMaxAsync`'s `#ItemWh` population now reads `Season` from `whboxitems.Season` directly**, not from `bfldata.dbo.pallettype.Season` via INNER JOIN. Same change pattern as 1.13.2 (WH Stock Position / Variance Report) and 1.14.7 (EOM Generate Division Summary) — keeps the rule consistent across every place we bucket boxes into Summer / Winter.
- Pallettype INNER JOIN dropped (was only used for `pt.Season`). Boxes whose `PalletType` had no row in the pallettype master are no longer silently lost from the per-`(Item, Div, Season)` WHBoxQty calculation.
- Added `UPPER()` for case-insensitive matching, consistent with the other rules.

### Affects
- `LPM_SimItemSkuMax` table contents: per-item `WHBoxQty` per `Season` may shift slightly:
  - Items whose PalletType is missing from `pallettype` master are now included.
  - Items where `w.Season` differs from `pt.Season` move to whichever season `w.Season` reports.
- Downstream effect: SKU Max rule band lookup may yield different `SKUMax` values for these items → SIM Generate may allocate them differently in subsequent runs.

### Notes
- Only `BuildItemSkuMaxAsync` is changed. Other queries in `LpmSimGenerator.cs` that still read `pt.Season` (snapshot building, allocation flow) are NOT touched — separate concerns.
- Run `Build SKU Max` to apply. Existing `LPM_SimItemSkuMax` rows won't change until the next rebuild.
- No schema change. No migrations.

---

## 1.14.8 — SKU Max exclusion rules: fix Shopname bridge + revert Rule 4 to HSCode (2026-05-12)

### Fixed
- **SKU Max Build's exclusion rules 1–5 were silently matching 0 rows** despite the source tables (`ExcludeExport_Planning` 430K, `RemoveItemsFromTransfer` 41K, `ExcludeItemsMFCS` 198, `ExcludeSubclass` 261, `DeptPriceMaxQty_MH4` 380) containing meaningful data. Audit table `LPM_SimItemSkuMaxExcluded` therefore showed only 2 distinct `SourceTable` values (`LPM_StoreDivAccess`, `LPM_StoreDeptAccess`) instead of 7.
- **Root cause:** the rules joined `<exclusion>.Shopname = snap.StoreID`. LPM's `StoreID` is hyphenated (`BFL-DXD`, `LFL-MCT`); legacy exclusion tables use concatenated `Shopname` (`BFLAVENUES`, `EX2KUWAIT`). They never matched. ItemCode joins direct (confirmed via probe query — same values appear on both sides where present).
- **Fix:** enriched `#SkuSnap` once with a `Shopname` column resolved via `OUTER APPLY dbo.DataSettings` (uses `(StoreID, SIMCountry)` to pick the country's row, falls back to a SIMCountry-IS-NULL row if any). Every rule below now joins on `snap.Shopname` instead of `snap.StoreID`. Rules 1, 2, 3, 5 just need this single column flip; Rule 4 also reverts to HSCode-based matching (see below).
- **Rule 4 reverted to HSCode + upcbarcodes bridge** per `034_lpm_sim_skumax_exclusions.sql` original spec. The previous "direct Itemcode" join produced 0 matches because `ExcludeItemsMFCS.Itemcode` is sparse — the table is keyed by `HSCode`. Re-applies the HSCode bridge from `usa.dbo.upcbarcodes` BUT pre-filters upcbarcodes to only the HSCodes present in `ExcludeItemsMFCS` (≤198 rows) so the SkuSnap × upcbarcodes join can't blow up to the 282-trillion-row space the 1.9.5 implementation hit.
- **Reversed XML doc comments** on `DivisionSummaryEom.WHStockPurchased` / `NonPurchased` / `EligibleStock` were already fixed in 1.14.7.

### Behavioural change to expect
- Next SKU Max Build will start producing **non-zero `excluded`** and **non-zero `price-capped`** counts. Could be large — `ExcludeExport_Planning` has 430K rows.
- `LPM_SimItemSkuMaxExcluded` audit table will show **7 distinct `SourceTable` values** instead of 2.
- Many items that were previously left at their per-Vol-Grp computed SKUMax will now drop to 0 (or be capped down).
- The build's "Applying exclusion rules…" phase will take slightly longer (Rule 4 now has the upcbarcodes lookup). Rule 5's 14-minute timing is unrelated (the `Hodata.SalesPrice` CROSS APPLY) and isn't touched in this release.

### Files
- `src/LpmSim.Data/LpmSim/LpmSimGenerator.cs` — `BuildItemSkuMaxAsync` only: snapshot enrichment + 5 rule joins + Rule 4 rewrite.

### Notes
- No schema change. No migration.
- Other reports / SIM allocation / EOM Generate untouched.
- If the new build produces TOO MANY exclusions and that surprises you, the migration spec (`034_lpm_sim_skumax_exclusions.sql`) is the authoritative source of which keys each rule matches — review against current data.

---

## 1.14.7 — EOM rule unification + theme revert to yellow (2026-05-12)

### Changed — theme
- **Sidebar + table-head reverted from light blue (1.14.6) back to golden yellow** (`#FBC02D` family, same values as 1.14.5). Hover/active/icon/subtitle colors all flipped back to the amber palette. Dark text + icons unchanged.

### Changed — EOM Division Summary stock breakdown
- **EOM Generate's Division Summary stock-breakdown query now follows the same WH-side rule as the WH Stock Position and Variance Report.** Three tweaks in `EomCalculator.GetDivisionStockBreakdownAsync`:
  1. **Purchased filter strict:** `ShopEligible IS NULL OR <> 'E'` → **`ShopEligible <> 'E'`**. Matches the planner's SSMS reference query (`WHERE ShopEligible <> 'E'`). NULL ShopEligible rows no longer count as Purchased.
  2. **PalletCategory from whboxitems direct:** was `pt.PalletCategory` via `INNER JOIN bfldata.dbo.pallettype pt`. Now reads `w.PalletCategory` straight from the source. Boxes whose PalletType is missing from the pallettype master are no longer silently dropped.
  3. **Season from whboxitems direct:** was `pt.Season` (also from the pallettype join). Now reads `w.Season`. Same rule (`'W'` → Winter, else Summer).
- HO side unchanged — still derives season from `upcbarcodes.Itemtype`.

### Affects
- ONLY the **Division Summary** tab's 4 stock columns: HO Stock, WH Stock (Purchased), WH Stock (Non-Purchased), Eligible Stock.
- Other columns on the Division Summary tab (SOH / Target EOM / Target Sales / Merch Need / LPM Box Qty) come from `LPM_EOM_Output` and are NOT affected.
- The EOM calculation engine (the 6-step algorithm) and the saved `LPM_EOM_Output` rows are NOT affected.
- SIM Generate and downstream allocation are NOT affected.

### Expected number shifts
- WH Stock (Purchased) may **drop slightly** — rows with `ShopEligible IS NULL` no longer count.
- All 4 columns may **rise slightly** — boxes with PalletType not in pallettype master are no longer lost.
- Summer/Winter split may move for individual boxes if `whboxitems.Season` differs from the joined `pallettype.Season`.

### Fixed
- **Reversed XML doc comments** on `DivisionSummaryEom.WHStockPurchased` and `WHStockNonPurchased` in `EomGenerate.razor` (lines 977–980). The data was always correct; the inline `<summary>` tags were swapped. Now updated to match the SQL.

### Notes
- No schema change, no migrations. Behavioural change is confined to the 4 informational columns on the Division Summary tab.

---

## 1.14.6 — Theme light blue + EOM page-busy fix + SIM Summary timeout (2026-05-12)

### Fixed
- **EOM Generate "page still busy" lag after Generate / Approve / View Saved.** The Snackbar popup ("Generated 1,176 rows") was firing BEFORE the heavy Division-Summary stock-breakdown CTE finished, so the page looked frozen for several extra seconds even though the data was already loaded. That eager call was redundant — the breakdown was already wired (1.13.2) to lazy-load on the Division Summary tab click. Removed the eager calls from all three handlers (`GenerateAsync`, `ViewSavedAsync`, `ApproveAsync`); each now just resets the cached breakdown so the next Division Summary tab click refetches fresh data. The page unblocks as soon as `_preview` is populated.
- **SIM Generator → Summary "Execution Timeout Expired" error.** `LpmSimReports.ExecAsync` (the shared helper that runs every SIM-Reports SQL — EOM Summary, Store Summary, Box Detail, Allocation Trace, Custom Report) had `CommandTimeout = 180s`. Bumped to **600s** so larger batches don't hit the ceiling. If a single query genuinely needs >10 minutes the right fix is DB-side (index / query plan), not another timeout bump.

### Changed
- **Theme flipped from golden yellow (1.14.5) back to a blue family, but lighter** — user feedback on yellow led to the swap. Sidebar bg is now Tailwind **`#93C5FD`** (blue-300, soft light blue). Brand-strip uses **`#60A5FA`** (blue-400) for visual separation from the main nav. Every report's MudTable header strip matches at `#93C5FD`.
- **Dark text + icons** (`#0F172A` near-black) carried over unchanged from 1.14.5 — both yellow and light-blue need a dark foreground for contrast, so no foreground recoloring was needed.
- **Hover / active highlights** re-tuned to blue tones: hover = `#BFDBFE` (blue-200), active = `#DBEAFE` (blue-100), active border-left + icons use `#1E40AF` (blue-800) as the marker. Same lighter-than-bg pattern that worked in 1.14.5, now in blues.
- **Column subtitles** that surface filter rules / totals (`.lpm-th-total`, `.th-total`, the WH Stock "excl. NON ELIGIBLE, ECOM" inline) recoloured from deep amber `#854D0E` to deep blue `#1E40AF` for readability on the new bg.

### Files
- `wwwroot/app.css` — CSS variables + hover/active rules + table-head subtitles.
- `Components/Layout/MainLayout.razor` — MudBlazor `PaletteLight.DrawerBackground` (kept in lock-step with the CSS var).
- `Components/Layout/NavMenu.razor.css` — scoped hover/active highlights.
- `Components/Pages/LPM/Reports/WhHoStock.razor` and `VarianceReport.razor` — inline subtitle color.

### Notes
- No business logic changes. Pure theme.

---

## 1.14.5 — Theme: sidebar + table headers to golden yellow (2026-05-12)

### Changed
- **Sidebar (drawer) and every report's table-header strip** switched from blue (`#1e3a8a`, 1.10.x palette) to golden yellow (`#FBC02D`) per user request. Brand-strip in the sidebar header uses a deeper amber (`#F9A825`) to keep the brand block visually distinct from the nav.
- **Foreground text** on the yellow surfaces flipped from light-slate to near-black (`#0F172A`) for contrast. Muted text (section labels like "LPM VARIABLES") moved from light-gray to slate-600 (`#475569`).
- **Hover and active states** on nav links re-tuned: hover = amber-300 (`#FCD34D`), active = yellow-300 (`#FDE047`) with a deep-amber (`#854D0E`) left-border marker. Pre-1.14.5 these were pale-yellow values that vanished on the new yellow background.
- **Column subtitles** that surface filter rules / count totals on the table headers (e.g. "excl. NON ELIGIBLE, ECOM" on WH Stock; row-count subtitles on Warehouse Box Details) switched to deep amber (`#854D0E`) and slate-600 for readability on yellow.
- **Sign-out button** in the sidebar header recoloured (dark text + dark alpha border; white hover background).

### Files
- `wwwroot/app.css` — CSS variables `--lpm-drawer-bg`, `--lpm-drawer-bg-strong`, `--lpm-drawer-text`, `--lpm-drawer-muted`, `--lpm-table-head` plus the hard-coded values that referenced them.
- `Components/Layout/MainLayout.razor` — MudBlazor `PaletteLight.DrawerBackground` / `DrawerText` / `DrawerIcon` (kept in lock-step with the CSS var because MudBlazor injects an inline style that beats `:root`).
- `Components/Layout/NavMenu.razor.css` — scoped hover/active highlights.
- `Components/Pages/LPM/Reports/WhHoStock.razor` and `VarianceReport.razor` — inline subtitle color on the WH Stock column header.

### Notes
- No business logic changes. Just colors.
- Other elements (page subtitles in the white content area, metric cards, alerts, forms) keep their existing palette.

---

## 1.14.4 — EOM Generate: rename Approve → Generate & Approve (2026-05-12)

### Changed
- **The "Approve" button on the EOM Generate page is renamed to "Generate & Approve"** to reflect what it actually does. The button calls `Eom.GenerateAsync` which both runs the EOM engine AND saves to `LPM_EOM_Output` in a single call — a prior click on "Generate" is NOT required and a previous in-memory preview is NOT used (Approve always re-calculates). The old label implied "approve what was just previewed", which was misleading and led the planner to think a Generate click alone would save.
- Confirmation dialog title and primary button text updated to match. Success / failure Snackbar messages now say "Generated & approved …" / "Generate & Approve failed …".

### Notes
- **The standalone "Generate" button is unchanged** — still preview-only, in-memory, useful for sanity-checking before committing.
- Internal method name (`ApproveAsync`) stayed put — only the user-facing labels changed.
- No business-logic change. Same SQL, same data, same output rows.

---

## 1.14.3 — EOM Generate: more parallelism (2026-05-12)

### Performance
- **Two further EOM Generate perf wins on top of 1.13.2.** The page-open path was still spending real time inside `GetSavedAsync` (the saved-output fetch) — that work now runs concurrently with the readiness check and is itself parallelised internally.
  1. **`CheckAsync` and `GetSavedAsync` now run in parallel** on the Razor side via `Task.WhenAll`. They share no state, so the page-open wait is now `max(check, saved)` instead of `check + saved`.
  2. **The 3 independent queries inside `GetSavedAsync`** — DataSettings (store names), Divisions (division names), and LpmEomOutputs (saved rows) — also run in parallel, each with its own DbContext. Same pattern as `CheckAsync` from 1.13.2.

### Notes
- No SQL change, no schema change. Just reshapes the await-graph.
- All three GetSavedAsync callers (page open / View Saved button / post-Approve refresh) benefit.

---

## 1.14.2 — Variance Report: split Itemmaster lookup, fix 0-rows bug (2026-05-12)

### Fixed
- **Variance Report was returning 0 rows even when divisions with known variance were selected** (and the load was slow). Root cause: the inline `LEFT JOIN HODATA.dbo.Itemmaster` cross-database join was either confusing the SQL Server query planner OR silently filtering due to an implicit type mismatch between `Itemmaster.Itemcode` and `LPM_LocStock.ItemCode`. Moved the description lookup to a **separate C# round-trip** after the main variance query completes — same end result, much more reliable.
- Variance query now runs without any cross-DB join; the second query pulls only the descriptions for the distinct itemcodes that came back, with an explicit `CAST(Itemcode AS nvarchar(64))` on both sides of the IN-clause to neutralise any type-mismatch.
- Description lookup wrapped in a try/catch — if HODATA can't be reached, the page still shows variance numbers with blank descriptions instead of failing outright.

### Notes
- No schema or business-rule change. Same data, same totals, same ABS(Variance) DESC sort.
- Expect faster load times in addition to the bug fix — single-DB query plans are simpler than cross-DB ones.

---

## 1.14.1 — Variance Report: remove 10K row cap (2026-05-12)

### Changed
- **The 10,000-row safety cap on the Variance Report is removed.** Real-world prod data has more than 10K items contributing to the total variance, so the cap was hiding rows the planner needs to investigate. Result set is now unbounded — every (Item × Division) row where HO ≠ WH is returned.
- Removed the "Result capped at 10,000 rows" Snackbar warning and the metric-card subtitle hint.

### Notes
- `MudTablePager` still paginates the on-screen display (50 / 100 / 200 / 500 per page selectable).
- Excel export captures the full row set, not just the visible page.
- If a country's variance grows to very large row counts and the page feels slow, narrow via the Division multi-select or Itemcode contains-search before hitting Load.

---

## 1.14.0 — Reports → Variance Report (item-level) (2026-05-12)

### Added
- **New report: Variance Report** at `/lpm/reports/variance`, listed under the **Reports** sidebar group below "WH Stock Position". Item-by-item breakdown of the gap between Head Office stock and Warehouse stock — the same aggregation as WH Stock Position but rolled up at `ItemCode × Division` instead of Division alone, filtered to rows where **HO ≠ WH** so the planner can drill into the source of any division-level variance.
- **Columns:** Itemcode | Item Name | Division | HO Stock | WH Stock | Variance.
- **Filters:** Country (single), Division (multi-select), Season (All / Summer / Winter, default All), free-text Itemcode contains-search.
- **Excel export** with the same 6 columns + a TOTAL row.
- **Sorted** by `ABS(Variance) DESC` server-side so the biggest gaps surface first. Click any column header to re-sort in-memory.
- **Top 10,000 row safety cap** with a Snackbar warning if hit (variance-only filter keeps real-world cases well under).

### Data sources
- **HO Stock:** `racks.dbo.LPM_LocStock.SOH` where `storeid IN (...)` — UAE uses literal `'HODATA'`; other countries pull every storeid where `ExportWH='Y'` from `bfldata..DataSettings` (same logic as WH Stock Position).
- **WH Stock:** `whboxitems.Qty` applying the universal WH rule `ShopEligible <> 'E' AND PalletCategory NOT IN ('NON ELIGIBLE', 'ECOM')` — identical to WH Stock Position so the variance numbers reconcile across the two reports.
- **Item Name:** `HODATA.dbo.Itemmaster.description`. Single global source for all countries — if non-UAE has a different item-master DB later, the join can be swapped to a country-aware `[<DataName>].dbo.Itemmaster` via the same `WhBoxItemsSource`-style helper.
- **Division mapping:** `upc_subclass × subclassmaster` (LEFT JOIN; items with no mapping bucket as `(no division)` — same pattern as WH Stock Position).

### Notes
- Variance shown in red when negative (WH > HO — over-stocked vs HO).
- No schema changes. New service registered in DI as `VarianceReportService`.
- Variance ABS-sort and the 10K cap together mean even on a huge catalog the planner gets the most actionable rows first.

---

## 1.13.3 — Reports: rename WH/HO Stock → WH Stock Position (2026-05-12)

### Changed
- **Renamed the WH/HO Stock report to "WH Stock Position"** across the sidebar label, page title, page heading, Excel sheet name, Excel filename prefix (now `WhStockPosition_<Country>_<Timestamp>.xlsx`), and audit-log category (`Reports.WhStockPosition.Load`). Same page, same columns, same SQL — naming only.

### Notes
- **URL preserved at `/lpm/reports/wh-ho-stock`** so any existing bookmarks keep working. Change the route on request.
- Internal C# type names (`WhHoStockService`, `WhHoStockRow`, `WhHoStockFilter`, `WhHoSeason`) and the source file (`WhHoStock.razor`) were intentionally left untouched — they're developer-facing only and renaming would be a wider refactor.

---

## 1.13.2 — EOM perf + WH/HO Stock PalletCategory source (2026-05-12)

### Performance — EOM Generate page load
- **EOM Generate page-open time cut dramatically.** The "Re-check" / page-open path previously ran 9 sequential DB queries (8 readiness checks + the Division Summary stock breakdown). Two changes here:
  1. **Readiness queries now run in parallel.** The 8 independent checks (weights, store IDs, division count, planned inputs, WH stock count, store grades, volume groups, SKU max rules) fire concurrently via `Task.WhenAll`, each with its own DbContext (EF Core's DbContext isn't thread-safe). The single dependent query (sales-turns count) runs after Wave 1 finishes. Total time drops from ~sum-of-each to ~max-of-each — typically a 70–80% reduction on this path.
  2. **Division Summary stock breakdown is now lazy.** The heavy multi-CTE `GetDivisionStockBreakdownAsync` query (LPM_LocStock + whboxitems + upc_subclass + pallettype, FULL OUTER JOIN, no filters) is no longer run on page open. It fires the first time the planner clicks the **Division Summary** tab, and is cached per `(country, year, month)` scope so re-clicking the tab without changing filters is instant.

### Changed — WH/HO Stock report data source
- **PalletCategory is now read from `whboxitems.PalletCategory` directly** (was sourced via `INNER JOIN bfldata.dbo.pallettype pt` on `PalletType`). Two effects:
  - Boxes whose `PalletType` has no row in the pallettype master table are no longer silently dropped from the report — they now flow through and bucket by whatever category is stored on their own row.
  - One fewer cross-table dependency, slightly faster query.
- All 5 category-aware columns (WH Stock, Reserved Stock, Seasonal Stock, On Hold Stock, Eligible Stock) updated to use `w.PalletCategory`.

### Changed — WH/HO Stock universal eligibility rule
- **All `whboxitems`-sourced columns now apply the same rule:**
  - `ShopEligible <> 'E'` *(strict — excludes both 'E' and NULL, matching the planner's reference SSMS query exactly)*
  - **AND** `PalletCategory NOT IN ('NON ELIGIBLE', 'ECOM')`
- Reserved / Seasonal / On Hold / Eligible columns: the category clause is implicit (they match a specific category), so only the `ShopEligible` clause was tightened from `IS NULL OR <> 'E'` → strict `<> 'E'`.
- **LPM Stock and Non-LPM Stock** previously had NO restrictions (pure LPM-column check). Now they also apply the universal rule, so totals across columns reconcile and the planner's raw SSMS validation queries match the report.
- The column header on **WH Stock** displays the rule inline: "excl. NON ELIGIBLE, ECOM".

### Performance — WH/HO Stock report
- **WH side per-row `OUTER APPLY` replaced with `LEFT JOIN ItemDiv`** (the pre-aggregated CTE that already maps every itemcode → its division). The previous pattern ran a subquery against `upc_subclass × subclassmaster` for every row in `whboxitems` — N+1 against a multi-million-row table, which dominated load time. Single JOIN is index-friendly and runs once.
- Division filter logic consolidated to a single fragment (both CTEs now reference the same `id.Division` alias).

### Notes
- Same UI shape, same column meanings on Reserved / Seasonal / On Hold / Eligible. LPM / Non-LPM are now stricter — if you compared them against raw SSMS queries before today, expect smaller numbers now (rows with `ShopEligible='E'` or category in NON ELIGIBLE/ECOM no longer count).
- No schema changes, no migrations.

---

## 1.13.1 — Reports → Sidebar group + HO/WH totals reconcile (2026-05-12)

### Changed
- **Reports section in the sidebar** converted from a flat header + always-visible link to a collapsible `MudNavGroup`. The "Reports" parent row is shown by default with a chevron; sub-items (currently just "WH / HO Stock") only render when the planner expands the group. Matches the standard MudBlazor nav-group UX so future Reports entries don't crowd the sidebar.

### Fixed
- **WH / HO Stock report — HO Stock total was under-reporting** by the SOH of items that have no `upc_subclass → subclassmaster` mapping (~4% in UAE prod). The CTE used an `INNER JOIN ItemDiv`, which silently dropped any LPM_LocStock row whose ItemCode wasn't in the mapping table. Switched to `LEFT JOIN ItemDiv` and bucket unmapped items as a **"(no division)"** row at the bottom of the table. The page total now matches `SELECT SUM(SOH) FROM LPM_LocStock WHERE storeid IN (...)`.
- Same fix applied to the WH side: `OUTER APPLY (...) sm` now defaults `sm.Division` to `'(no division)'` via `ISNULL(...)`, and the previous `WHERE sm.Division IS NOT NULL` filter was removed, so WH-side totals also reconcile against a raw `SUM(Qty)`.
- When a specific Division (or set) is selected in the multi-select, the `'(no division)'` bucket is naturally excluded (no one filters for it explicitly).

### Notes
- Only the Reports section's sidebar markup changed. Existing flat sections (LPM Variables, Planning Config, Admin) stay as `<div class="lpm-nav-section">` headers.
- HO + WH SQL changes are isolated to `WhHoStockService.cs` — no schema changes, no impact on EOM Generate / SIM Generate / Warehouse Boxes queries.

---

## 1.13.0 — Reports → WH / HO Stock comparison report (2026-05-12)

### Added
- **New sidebar group "Reports"** (positioned between Warehouse Boxes and Planning Config) with the first item **WH / HO Stock**.
- **WH / HO Stock report page** at `/lpm/reports/wh-ho-stock`: one row per Division comparing Head Office stock vs Warehouse stock, with a per-category breakdown. 10 columns: Division, HO Stock, WH Stock, Variance, Reserved, Seasonal, On Hold, Eligible, Non-LPM, LPM. Bold TOTAL footer row. Variance turns red when negative.
- **Filters:** Country (single), Division (multi-select via the existing MultiSelectFilter component), Season (single — All / Summer / Winter, default All).
- **Country drives both sides of the report.** WH side uses the same UAE↔non-UAE source switch as the Warehouse Boxes page (`racks.dbo.whboxitems` vs `[<DataName>].dbo.WHBoxItemsExport`). HO side resolves storeids per country: UAE = literal `'HODATA'`; other countries = every `storeid` from `bfldata..DataSettings` where `country=<dataname>` and `ExportWH='Y'` (summed across multiple stores if present).
- **Excel export** mirrors the table including the TOTAL row.

### Column definitions
- **HO Stock** = Σ `LPM_LocStock.SOH` for the country's HO storeids, mapped to Division via upc_subclass → subclassmaster.
- **WH Stock** = Σ `whboxitems.Qty` where `UPPER(PalletCategory) <> 'NON ELIGIBLE'` AND purchased (`ShopEligible IS NULL OR <> 'E'`).
- **Variance** = `HO Stock − WH Stock`.
- **Reserved / Seasonal / On Hold / Eligible Stock** = Σ Qty where `UPPER(PalletCategory)` matches the category AND purchased.
- **Non-LPM / LPM Stock** = Σ Qty based purely on `whboxitems.LPM` (null/empty vs populated). No category or purchased restriction — per user spec.
- **Season filter** applies on both sides: WH side reads `whboxitems.Season` directly (`'W'` = Winter, else Summer); HO side derives from `UPCBarcodes.Itemtype`.

### Notes
- No schema changes, no migrations. New service registered in DI as `WhHoStockService`.
- Items with no subclass → division mapping are excluded (would land in an unrecognised-division bucket otherwise).
- "On Hold" and "Non Eligible" are compared case-insensitively via `UPPER(...)` so the SQL doesn't care whether the master tables stored the categories as `'On Hold'`, `'ON HOLD'`, or `'on hold'`.

---

## 1.12.0 — WH Boxes: TrnDate / CurrDate columns + date-range filter (2026-05-12)

### Added
- **Two new columns on the Warehouse Boxes detail view (Box Detail mode only):** `TrnDate` (date) and `CurrDate` (datetime), shown as the last two columns of the on-screen table and the Excel export. Sourced directly from `whboxitems.TrnDate` and `whboxitems.CurrDate` (UAE) or the equivalent columns on `[<DataName>].dbo.WHBoxItemsExport` (other countries) — both source tables expose them under the same names. Dates display as `yyyy-MM-dd` / `yyyy-MM-dd HH:mm`.
- **TrnDate range filter:** two new date pickers (`TrnDate From`, `TrnDate To`) on the filter bar, both empty by default. Inclusive on both ends. The pickers highlight when set (same visual cue as the other "non-default" filters on the page). CurrDate is **not** filterable — by design, only `TrnDate` is.

### Notes
- Summary modes (Division / Department / Brand) are unchanged — dates only surface on Box Detail.
- TrnDate values survive across country switches (the dates aren't country-scoped, so `OnCountryChanged` doesn't clear them).

---

## 1.11.0 — WH Boxes: multi-select filter dropdowns (2026-05-12)

### Added
- **Multi-select checkboxes on the Warehouse Boxes filter bar.** Eight previously single-value dropdowns are now multi-select via the existing `MultiSelectFilter` component (same UX as the SIM Generator's Division/Store filters): **Warehouse, Type Name, Pallet Category, LPM (month), Division, Department, Brand, ContNo**. Planners can now narrow a load to, say, three warehouses + two divisions in a single shot instead of running one load per combination.
- The three non-list filters stay single-select: **Country** (data source switch), **LPM Status** (enum), **Group By** (mode switch).

### Changed
- `WhBoxFilter` record (`LpmSim.Data/Warehouse/WarehouseQueryService.cs`): the eight list-type fields changed from `string?` to `IReadOnlyList<string>?`. `null`/empty list means "no filter" (same as before for missing values).
- SQL now uses parameterized `IN (@p0, @p1, …)` clauses built by a new `BuildInClause` helper. Empty lists produce no fragment, so an unfiltered field still generates zero predicates (no perf regression vs. the old `IS NULL OR =` pattern).
- Division/Department clauses route to either `HAVING` (in `GetBoxesAsync`, which aggregates with `MAX(scm.Division)`) or `WHERE` (in the three summary queries, which read `sm.Division` directly) via a single `BuildFilterClauses(filter, divDeptInHaving)` helper — keeps the routing in one place.
- `OnCountryChanged` now clears the country-scoped selections (Warehouse, LPM, ContNo, Brand) instead of nulling single fields, since those lists are repopulated from the new country's data.

### Notes
- The `MultiSelectFilter` component is unchanged — same component used elsewhere in the app, so the UX (search box + checkbox list + Done button + "N selected" trigger label) is identical.
- No schema changes. No data migration. Just deploy.

---

## 1.10.8 — Real fix for sidebar/table header color mismatch (2026-05-11)

### Fixed
- **Sidebar and table headers were rendering different shades of blue** despite 1.10.6/1.10.7 setting both `--lpm-drawer-bg` and `--lpm-table-head` to `#1e3a8a`. The CSS variable for the drawer was being overridden by MudBlazor's `PaletteLight.DrawerBackground` in `MainLayout.razor`, which still pointed at the old slate `#1e293b` — MudBlazor injects that as an inline style with higher specificity than the CSS variable rule.
- Updated `PaletteLight.DrawerBackground` from `#1e293b` → `#1e3a8a` so it agrees with the CSS variable. Sidebar and every table header now render the same `#1e3a8a` blue.

### Notes
- The same kind of MudTheme-vs-CSS-variable override could affect other tokens (`Surface`, `Background`, etc.) — but for now everything else is unchanged and looks consistent. If something else looks off later, check both `MainLayout.razor`'s palette AND `app.css`'s `:root` variables.

---

## 1.10.7 — Table headers: slate → blue (2026-05-11)

### Changed
- **All `MudTable` header strips** switched from slate (`#0f172a`) to **`#1e3a8a`** (Tailwind blue-900), matching the sidebar palette from 1.10.6. Affects every report grid in the app — EOM Generate (Store × Division, Store Summary, Division Summary), SIM Generate result tabs, SIM Reports (Item Details, Custom Report, Box Detail, etc.), Warehouse Boxes, Production Schedule, ADM, Audit Log, Weekly Sales Target Split, Store / Division Access, Store / Department Access, Warehouse Priorities, Grades, Volume Groups, SKU Max Rules, Users.
- Single CSS variable: `--lpm-table-head` at the top of `app.css`. All tables pick it up via the `.mud-table-head th` rule.

### Notes
- Text on the header stays white/light for contrast.
- No functional change. Pure color theme update.

---

## 1.10.6 — Sidebar palette: slate → blue (2026-05-11)

### Changed
- **Navigation drawer (sidebar) background** switched from slate (`#1e293b` / `#0f172a` — visually near-black) to a clearly blue palette: `#1e3a8a` (Tailwind blue-900) body, `#172554` (blue-950) for the header strip. Matches the existing `#2563eb` accent family.
- `--lpm-drawer-muted` brightened from `#94a3b8` → `#cbd5e1` so section labels (LPM VARIABLES, PLANNING CONFIG) stay readable on the lighter blue.

### Notes
- Only the sidebar's color tokens changed — main page content, tables, and forms keep their existing light theme.
- All 4 colors live as CSS variables at the top of `app.css`. If you want to tweak the exact shade later, edit `--lpm-drawer-bg` and `--lpm-drawer-bg-strong` — every navigation element picks them up automatically.

---

## 1.10.5 — Full MudBlazor v7 analyzer cleanup sweep (2026-05-11)

### Fixed
- **MUD0002 on `MudTextField` / `MudSelect` / `MudAutocomplete` / `MudDatePicker`** — 1.10.4 fixed only the `MudNumericField` instances; the deprecation also applies to other form-field components. Bulk-removed `Dense="true"` from every line where it appeared alongside `Margin="Margin.Dense"` across 18 Razor files (58 occurrences total). The legacy `Dense` parameter is deprecated on all form-field components in MudBlazor v7; `Margin="Margin.Dense"` (already present everywhere) is the v7 way to get compact spacing.
- **MUD0002 on `MudIconButton` `Title=`** in `WarehousePriorities.razor` and `WeeklySalesTargetSplit.razor` — renamed to lowercase `title=`. The analyzer was flagging `Title` (PascalCase) as a non-existent component parameter; lowercase `title` makes it explicit that this is the standard HTML `title` attribute (browser tooltip) being passed through.

### Notes
- `Dense="true"` is still valid (and still used) on non-form components: `MudTable`, `MudCheckBox`, `MudAlert`, `MudChip`. Those weren't flagged and were left alone.
- No functional change. Pure analyzer cleanup.

---

## 1.10.4 — MudBlazor v7 analyzer warnings cleanup (2026-05-11)

### Fixed
- **MUD0001** — `IsInitiallyExpanded` on `MudExpansionPanel` in `EomGenerate.razor` renamed to `Expanded` (MudBlazor v7 dropped the `IsInitiallyExpanded` naming).
- **MUD0002** — `Dense="true"` removed from 7 `MudNumericField` instances across `WarehousePriorities.razor`, `WarehousePriorityAddDialog.razor`, `LpmSimGenerate.razor` (2x), and `ProductionSchedule.razor` (3x). MudBlazor v7 form components only honour `Margin="Margin.Dense"` for compact spacing; the legacy `Dense` parameter is deprecated. `Margin="Margin.Dense"` was already present alongside `Dense="true"` in every flagged location, so removing `Dense="true"` is a net-zero visual change.

### Notes
- `Dense="true"` is still used (and still valid) on non-form components like `MudTable`, `MudCheckBox`, `MudAlert`, and `MudChip`. Those weren't flagged by the analyzer and were left alone — they have their own `Dense` parameter that's unrelated to the form-field deprecation.
- No functional change. Pure analyzer cleanup.

---

## 1.10.3 — Invert ShopEligible semantics on EOM Division Summary stock columns (2026-05-11)

### Fixed
- **WH Stock (Purchased)** and **WH Stock (Non-Purchased)** columns in the EOM Division Summary tab had their `ShopEligible` filters swapped. My 1.10.0 implementation followed the SIM allocator's convention (`ShopEligible = 'E'` → claimed/purchased), but the business labels work the opposite way:
  - **WH Stock (Purchased)** = boxes where `ShopEligible IS NULL OR <> 'E'` (cleared past the 'E' in-process state)
  - **WH Stock (Non-Purchased)** = boxes where `ShopEligible = 'E'` (still being processed)
  - **Eligible Stock** = `PalletCategory = 'ELIGIBLE' AND (ShopEligible IS NULL OR <> 'E')` (purchased subset of the ELIGIBLE category)
- The SIM allocator's filter on `ShopEligible <> 'E'` (= "available to allocate") is untouched and continues to mean what it always did. Only the Division Summary's display labels were reversed.

### Notes
- After the deploy lands, refresh the EOM Generate page — the larger number should now appear in "WH Stock (Purchased)" and the smaller in "WH Stock (Non-Purchased)" (the opposite of what 1.10.0–1.10.2 showed).

---

## 1.10.2 — Fix EOM Store × Division header layout (2026-05-11)

### Fixed
- **Headers on the EOM Generate Store × Division tab were rendering as vertical letter-stacks** (`W t A v g S o l d`, `S o l d Q t y R a n k`, etc.) because the table tried to fit all 18 columns into the viewport width via percentage column widths + forced `overflow-x: hidden`. At any reasonable screen size the column widths shrank below the width needed for a single character, so the browser wrapped each character onto its own line.

### Changed
- Replaced the percentage-based column widths on the Store × Division `MudTable` with **fixed pixel widths** sized to each header's actual text (e.g. 140px for Store/Division, 70px for the 3-line `Wt/Avg/Sold`-style headers, 100px for `Merch Need (Month)`-style headers). Total table width ≈ 1,580px.
- Updated `.lpm-eom-preview` CSS — removed the `width: 100% !important; max-width: 100% !important; overflow-x: hidden !important;` block. The container now scrolls horizontally when columns exceed viewport width. Heading text stays on its intended 1-3 lines.

### Notes
- Removing the Wk1-Wk4 columns in 1.10.1 alone wasn't enough — even at 14 columns the percentage layout was squashing headers. This fix would have made 1.10.1's column removal unnecessary, but the per-week columns can stay out of this tab regardless (the Division Summary's Wk1-4 columns from 1.9.0 are still removed; per-week values remain stored on `LPM_EOM_Output` and SIM Generate's Week dropdown still picks them).
- The Store Summary and Division Summary tabs use different `MudTable` widths and weren't affected by the squash; left them as-is.

---

## 1.10.1 — Hide Wk1..Wk4 columns on the Store × Division grid (2026-05-11)

### Changed
- Removed the 4 per-week Merch Need columns (`Wk1`, `Wk2`, `Wk3`, `Wk4`) from the EOM Generate **Store × Division** tab. Their addition in 1.9.0 was squashing every other column header to the point that "WtAvgSold" rendered vertically as `W t A v g S o l d`, etc.
- The per-week values are still computed by `EomCalculator`, still persisted to `LPM_EOM_Output.MerchNeedWeek1..4`, and still drive SIM Generate's `Week` dropdown for the allocator's cap. Only the visual columns on this one grid are gone.
- Excel export rows from this tab were not touched (the Wk1..4 columns were inline in the on-screen table only — Excel writer already excluded them).

---

## 1.10.0 — EOM Division Summary: HO + WH stock columns with Season filter (2026-05-11)

### Added
- **Four new columns on the EOM Generate → Division Summary tab:**
  - **HO Stock** — `SUM(SOH)` from `racks.dbo.LPM_LocStock` where `dataname='HODATA'`, mapped to division via `upc_subclass × subclassmaster × Division`. Season per item from `usa.dbo.upcbarcodes.Itemtype` (`'W'` → Winter, else Summer).
  - **WH Stock (Purchased)** — `SUM(whboxitems.Qty)` where `ShopEligible = 'E'` (boxes already claimed by a shop).
  - **WH Stock (Non-Purchased)** — `SUM(whboxitems.Qty)` where `ShopEligible IS NULL OR <> 'E'` (still available for SIM).
  - **Eligible Stock** — `SUM(whboxitems.Qty)` where `pallettype.PalletCategory = 'ELIGIBLE' AND ShopEligible = 'E'` (purchased subset of ELIGIBLE category — ready-to-ship eligible items).
- **Season filter** on the Division Summary tab (`All seasons / Summer / Winter`). Filter applies ONLY to the 4 new stock columns — `SOH`, `Target EOM`, `Target Sales`, `Merch Need` columns stay aggregated across both seasons (they're per-store-div and don't carry season).
- **`DivisionStockBreakdown` record** in `LpmSim.Data.Eom.EomModels` — one row per `(DivCode, Season)` returned by the new `EomCalculator.GetDivisionStockBreakdownAsync(country, ct)`.

### Changed
- **`EomCalculator`** gains `GetDivisionStockBreakdownAsync` — a single-batch SQL with three CTEs (`ItemDiv`, `ItemSeason`, `HOByDiv`) and a `FULL OUTER JOIN` between HO and WH rollups. Returns instantly when the underlying tables have indexes on `(itemcode)`. Country-aware via `WhBoxItemsSource.ResolveAsync` so it works for UAE (`racks.dbo.whboxitems`) and other countries (`[<DataName>].dbo.WHBoxItemsExport`).
- **`EomGenerate.razor`** refetches the stock breakdown after every Preview / Generate / View Saved / Approve so the values match the underlying tables at the moment the user lands on the tab. The cached dictionary is keyed by `(DivCode, Season)`.

### Notes
- The 4 new columns are NOT persisted to `LPM_EOM_Output` — they're computed on demand because `LPM_LocStock` and `whboxitems` change daily and a stored snapshot would be stale. No migration required for this feature.
- If the HO-stock query produces no row for a given division (item not in `upc_subclass` or no rows in `LPM_LocStock` for `dataname='HODATA'`), the column shows `0` for that division. Same for WH stocks when no boxes exist.

---

## 1.9.6 — Rule 4 join fix: drop upcbarcodes detour (2026-05-11)

### Fixed
- **Rule 4 (`usa.dbo.ExcludeItemsMFCS`) was hanging the entire exclusions phase for 15+ minutes** in 1.9.5. The old SQL went through `usa.dbo.upcbarcodes` (**18,016,969 rows**) to map `ItemCode → HSCode`, then filtered on `(HSCode, Shopname)` against `ExcludeItemsMFCS`. Join space: 15.7M `#SkuSnap` × 18M `upcbarcodes` = potentially ~282 trillion combinations, even though `ExcludeItemsMFCS` itself has only **198 rows**.

### Changed
- **Rule 4 now joins `ExcludeItemsMFCS` directly on `(Itemcode, Shopname)`**:
  ```sql
  INNER JOIN usa.dbo.ExcludeItemsMFCS e
          ON e.Itemcode = snap.ItemCode
         AND e.Shopname = snap.StoreID;
  ```
  No `upcbarcodes` detour. The schema of `ExcludeItemsMFCS` already exposes `Itemcode` as a column, so this is the natural join. Drops Rule 4 from "many minutes" to milliseconds.
- Reason text updated from `"Item HSCode x Shopname excluded in ExcludeItemsMFCS"` → `"Itemcode x Shopname excluded in ExcludeItemsMFCS"` to reflect the actual match shape.

### Notes
- The previous behaviour (HSCode-cascade — one row blocks every item sharing the same HSCode) is no longer applied. If that was the intended business semantic for some legacy reason, tell me and I'll revert + add a covering index on `upcbarcodes (itemcode) INCLUDE (HSCode)` to make the detour fast.
- Row counts confirmed from the user's diagnostic on the live DB. All other rules' join sizes are reasonable:
  - Rule 1: 15.7M × 430K (`ExcludeExport_Planning`)
  - Rule 2: 15.7M × 20M (`upc_subclass`) × 8K (`ExcludeSubclass`) — fast if `upc_subclass.itemcode` is indexed (worth checking if Rule 2 timing is high in the next build's StageDetail)
  - Rule 3: 15.7M × 540K (`RemoveItemsFromTransfer`)
  - Rule 4: 15.7M × **198** (`ExcludeItemsMFCS`) — was 15.7M × 18M before this fix
  - Rule 5: 15.7M × small (`DeptPriceMaxQty_MH4` 380 rows + `Hodata.SalesPrice` for items only)
  - Rule 6: 15.7M × small (`#Deact` only contains deactivated rows)
  - Rule 7: 15.7M × 1K (`subclassmaster`) × small (`LPM_StoreDeptAccess`)

---

## 1.9.5 — Real fix for #NewSnap persistence (2026-05-11)

### Fixed
- **`Invalid object name '#NewSnap'`** still firing on 1.9.4. Dropping `BEGIN TRAN` (1.9.4's attempted fix) didn't address the actual root cause: the main INSERT command used `ExecuteReaderAsync` + manual `using var rdr = ... ; await rdr.ReadAsync(ct);` to fetch the staged row count. Combined with DDL statements (`CREATE CLUSTERED INDEX`) and DML (`SELECT INTO`) producing implicit "X rows affected" info messages, the reader was leaving the connection in a state that occasionally triggered a session reset on the next `SqlCommand` execution — wiping all session-scoped temp tables including `#NewSnap`. The fact that the staging count `15,755,634 staged` reached C# correctly proved the SELECT INTO succeeded; the failure was purely in the *next* command's view of session state.

### Changed
- Main INSERT command now:
  - Uses `SET NOCOUNT ON` to suppress the DDL/DML row-count notifications
  - Uses `ExecuteScalarAsync` (not `ExecuteReaderAsync`) — which is what every other count-returning command in this file already uses, including the temp-tables-setup commands that have always worked. ExecuteScalar drains the result set immediately after reading the scalar and doesn't leave the connection in an ambiguous state.
  - No longer adds unused `@country`, `@y`, `@m`, `@now`, `@user` parameters (the staging SQL doesn't reference them — those columns get added during the delta-apply phase later).

### Notes
- After 1.9.5 deploys, re-run **Build SKU Max** for the active period. Expected StageDetail:
  ```
  Done · X items · ... · Insert ~60s · 15.7M staged ·
  Delta XXs · N ins · M upd · K del · J unchanged ·
  Y excluded · M price-capped · K div-deact · L dept-deact
  [R1...R7 ms] [· R5 SKIPPED (Hodata...) until permission granted]
  ```
- If `Invalid object name '#NewSnap'` reappears in this version's StageDetail, the next step is the bigger refactor: merge the staging SELECT INTO into the same SqlCommand as the existing `#Deact` populate. Let me know.

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
