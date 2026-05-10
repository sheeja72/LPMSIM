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
