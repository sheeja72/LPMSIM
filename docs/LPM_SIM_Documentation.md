# LPM SIM — Complete Documentation

**Version**: 1.0
**Last updated**: April 2026
**Codebase**: `D:\AI Projects\LpmSim`

---

## 1. Purpose

LPM SIM is an internal on-prem web application used by BFL Planning to:

1. Configure planning variables (max quantities, monthly weights, planned turns/sales/EOM).
2. Compute the monthly **EOM (End-of-Month) plan** — a per-store, per-division allocation ceiling consisting of `TargetTurn`, `TargetSales`, `TargetEOM`, `VolumeGroup`, `SKUMax`.
3. Run the daily **LPM SIM** allocation — distribute items in eligible warehouse pallets to stores honoring SKU Max, SOH, Target EOM, and Volume Group priority.
4. Audit who did what, when, including read-only report queries.

The app is the operational successor to the Excel-based LPM workflow described in the original `LPM SIM1.docx` spec.

---

## 2. Architecture

| Layer | Technology |
|---|---|
| Web frontend | **Blazor Server** (Interactive Server rendering) |
| UI library | **MudBlazor 7.15** + custom CSS for compact tables, dark sidebar, metric cards |
| Web framework | **ASP.NET Core 9** (Kestrel by default; IIS-ready) |
| Auth | **Windows Authentication** (Negotiate / NTLM SSO) |
| ORM | **Entity Framework Core 9** with `Microsoft.Data.SqlClient` |
| Excel I/O | **ClosedXML 0.104** |
| Database | **Microsoft SQL Server** (mixed-mode auth, `LPM` SQL login) |

### Solution layout

```
D:\AI Projects\LpmSim\
├── LpmSim.sln
├── db\                                  # Idempotent SQL migrations
│   ├── 000_alter_division_pk.sql
│   ├── 001_create_lpmdivmax.sql
│   ├── 002_create_usermgmt.sql
│   ├── 003_create_auditlog.sql
│   ├── 005_createts_standardization.sql
│   ├── 006_phase_a_tables.sql
│   ├── 007_priorityrank_decimal.sql
│   ├── 008_planningmgr_skumax_scope.sql
│   ├── 009_lpmsim_output.sql
│   └── install_new_server.final.sql     # one-shot fresh install
├── docs\
│   └── LPM_SIM_Documentation.md         # this file
├── src\
│   ├── LpmSim.Core\                     # Domain entities, role/policy constants, ICurrentUser
│   ├── LpmSim.Data\                     # DbContext, EF mappings, EOM/LPM SIM/Warehouse engines, audit interceptor
│   └── LpmSim.Web\                      # Blazor pages, layout, auth, Program.cs
└── tests\LpmSim.Tests\                  # xUnit project (placeholder)
```

### Configuration

`src\LpmSim.Web\appsettings.json` carries the runtime connection strings:

```json
{
  "ConnectionStrings": {
    "Lpm":       "Server=BFL-LOGBACKUP\\LOGBACKUP;Database=LPMSIM;User Id=LPM;Password=...;TrustServerCertificate=True;Encrypt=False;",
    "Warehouse": "Server=BFL-LOGBACKUP\\LOGBACKUP;Database=RACKS;User Id=LPM;Password=...;TrustServerCertificate=True;Encrypt=False;"
  }
}
```

**User Secrets** (`%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`) override these and are explicitly loaded in `Program.cs` via `builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: false)`. Recommended for production-quality deployments.

---

## 3. Database

### 3.1 Servers

| Server | Role |
|---|---|
| `BFL-LOGBACKUP\LOGBACKUP` (production) | Hosts `LPMSIM`, `RACKS`, `BFLDATA`. Live target. |
| `192.168.10.72` (legacy / source) | Original `LPMSIM` and `RACKS`. Migration source — retained as backup. |

The application uses **two connection strings** because the LPM SIM allocation joins LPMSIM tables to `racks..whboxitems`, `racks..LPM_LocStock`, `racks..upc_subclass`, `racks..subclassmaster`, and `bfldata..pallettype`. All required databases sit on the same server, so cross-database 3-part naming works without linked servers.

### 3.2 LPMSIM tables (created by this app)

| Table | Purpose | Key |
|---|---|---|
| `LPMDivMax` | Per-store/division max quantity (legacy entry surface) | `(StoreID, DivCode)` |
| `LPMUser` | App user list (Windows usernames) | `Username` |
| `LPMRole` | Role catalog: Admin, Editor, Viewer, PlanningManager | `RoleCode` |
| `LPMUserRole` | User ↔ Role membership | `(Username, RoleCode)` |
| `LPMAuditLog` | Generic audit log: I/U/D + 'R' for action reads | `Id` |
| `LPM_EOM_Output` | Monthly per-store/div EOM plan | `(StoreID, DivCode, Year1, Month1)` |
| `LPM_SalesTurns` | Sold qty + Turns per (store, div, month) — Excel-uploaded | `(StoreID, DivCode, Year1, Month1)` |
| `LPM_MonthlyWeight` | 13-period weights per run (per Country) | `(Country, RunYear, RunMonth, PeriodSeq)` |
| `LPM_Planned` | Planned Turn / Sales Qty / EOM per (Country, Div, Month) | `(Country, DivCode, Year1, Month1)` |
| `LPM_StoreGrade` | Grade catalog (Diamond/Platinum/Gold/Silver/Bronze) with Share% + Markup% | `GradeCode` |
| `LPM_VolumeGroup` | Volume group catalog (A–E) with Share% | `GroupCode` |
| `LPM_WHStock` | Warehouse stock per (Country, Div, Month) — Excel-uploaded | `(Country, DivCode, Year1, Month1)` |
| `LPM_SKUMaxRule` | Lookup matrix `(Country, Div, VolumeGroup, WHStock range) → SKUMax` | `RuleId` |
| `LPMSIM_Batch` | Header per LPM SIM run: status (Draft/Approved), counts | `LPMBatchNo`, unique `(Country, RunDate)` |
| `LPMSIM_Output` | Allocation rows: one per (Box × Item × Store) | `Id` (FK `LPMBatchNo` cascade) |
| `LPMSIM_Batch_Backup` | Archived headers after Delete | `(LPMBatchNo, BackupTS)` |
| `LPMSIM_Output_Backup` | Archived line items after Delete | `(Id, BackupTS)` |

### 3.3 External tables consumed

| Table | DB | Used for |
|---|---|---|
| `dbo.DataSettings` | LPMSIM | Store master (StoreID, PBFullname, Country, ActiveStore). Read-only, mapped keyless. |
| `dbo.Division` | LPMSIM | Division catalog (DivCode 1–21, Name). |
| `dbo.whboxitems` | RACKS | Warehouse box × item lines (PalletNo, BoxNo, ItemCode, Qty, ShopEligible, LPMDt, PalletType, Warehouse, LPM, …). |
| `dbo.LPM_LocStock` | RACKS | SOH per (StoreID, Itemcode) across CostCode/Loccode rows. |
| `dbo.upc_subclass` | RACKS | Item → subclass mapping (`itemcode`, `MH4ID`). |
| `dbo.subclassmaster` | RACKS | MH4ID → DivID → **Division (text)**. ⚠ DivID space (401–421) does NOT match LPMSIM `Division.DivCode` (1–21); we map by the human-readable **Division name**. |
| `dbo.pallettype` | BFLDATA | PalletType → TypeName, PalletCategory (ELIGIBLE / NON ELIGIBLE / Non Trade / On Hold). |

### 3.4 Standardized columns

Every LPM_* and LPMSIM_* table has a `CreateTS datetime2(0) DEFAULT SYSDATETIME()` column. Tables that support edits also have `UpdatedTS` and `UserID`.

---

## 4. Authentication & Authorization

### 4.1 Windows Auth

`Program.cs` registers `AddNegotiate()` so the browser sends NTLM/Kerberos credentials silently. There's no login screen — domain-joined users are SSO'd. `LpmClaimsTransformer` runs on every authenticated request:

1. Reads `User.Identity.Name` (e.g., `BFLDOMAIN\sheeja`).
2. Looks up `LPMUser` row + role assignments.
3. Clones the principal, adds an `lpm_active` claim if the user is provisioned, and a `ClaimTypes.Role` claim per role.

Unprovisioned users see an **Access Denied** page (`Components\Shared\AccessDenied.razor`) showing their Windows username and instructing them to ask an admin.

### 4.2 Roles

| Role | Pages |
|---|---|
| `Admin` | Everything, including `/admin/users` and `/admin/audit`. |
| `PlanningManager` | All planning config: Grades, Volume Groups, SKU Max Rules + every LPM Variables page. |
| `Editor` | All LPM Variables pages (Division Max Entry, Monthly Weights, Planned Inputs, Uploads, EOM Generate, LPM SIM Generate, Warehouse Boxes, Reports). |
| `Viewer` | Read-only access (no pages currently bound exclusively, but the role exists for future use). |

Constants live in `LpmSim.Core/Roles.cs`:
- `Roles.Admin`, `Roles.Editor`, `Roles.PlanningManager`, `Roles.Viewer`
- `Roles.AdminOrEditor`, `Roles.AdminOrPlanner`, `Roles.AdminOrEditorOrPlanner`

### 4.3 Authorization gates

Default fallback policy: `RequireLpmUser` (authenticated **and** `lpm_active=1`). Pages override with `[Authorize(Roles = ...)]`. Bootstrap admin: `BFLDomain\sheeja` is seeded by `db/002_create_usermgmt.sql`.

---

## 5. Pages

### 5.1 Home (`/`)

Role-gated cards linking to the major workflows. Shows the current Windows user.

### 5.2 Division Max Entry (`/lpm/div-max`)

Country picker → loads all stores × divisions in that country. Inline editable Max Qty per row with optional store + division autocomplete filters and column sorts. Save upserts to `LPMDivMax`.

### 5.3 Monthly Weights (`/lpm/monthly-weights`)

13-period weight grid per (Country, RunYear, RunMonth). Defaults to UAE + the latest saved month. Buttons: **Auto-fill periods** (creates rows for the 12 prior months + current partial), **Copy prev month**, **Save**. Weight values are integer percentages (no decimals); the active total must equal 100%. Enter / Tab moves focus to the next period's weight cell.

### 5.4 Planned Inputs (`/lpm/planned`)

Per (Country, Division, Year, Month) entry of `PlannedTurn` (1 decimal), `PlannedSalesQty` (integer), `PlannedEOM` (integer). Defaults to UAE + latest saved month. Buttons: **Add all divisions**, **Copy prev month**, **Save**. Enter navigates row-by-row down the current column, then jumps to the next column.

### 5.5 Data Uploads (`/lpm/uploads`)

Tabbed Excel uploader.

- **Sales & Turns** — header `StoreID, Division, Year, Month, SoldQty, Turns`. Division accepts code or name. Validation messages identify the exact row, column letter, and reason (e.g., `Year (col C) is blank or not a number`). Upserts into `LPM_SalesTurns`.
- **WH Stock** — header `Country, Division, Year, Month, WHStockQty`. Same validation pattern. Upserts into `LPM_WHStock`.

Each tab has Download Template, drag-drop dropzone, row-level preview (errors only), commit button. Cell reads use `cell.Value` (the safe `XLCellValue` struct) so blank/error cells never throw.

### 5.6 EOM Generate (`/lpm/eom/generate`)

Single-page workflow:

1. **Filters**: Country (defaults UAE) / Year / Month (defaults to latest saved month or `LPM_EOM_Output` row).
2. **Readiness cards** (7): Weights, Planned, Sales/Turns, WH Stock, Grades, Volume Groups, SKU Max Rules — each green when valid, red with a specific blocker.
3. **Buttons**: Re-check, **View Saved**, **Preview**, **Generate**.
   - **View Saved** loads existing `LPM_EOM_Output` rows for the scope (no recompute).
   - **Preview** runs the engine in memory (no DB write).
   - **Generate** runs the engine and writes transactionally — DELETE existing rows for `(Country, Year1, Month1)` then INSERT new rows. Audited.
4. **Source pill** above the result table: green "Generated YYYY-MM-DD HH:MM" for saved data, amber "Live preview — not saved" for in-memory previews.
5. **Result table** (12 cols, fits without horizontal scroll, headers wrap to 2–3 lines): Store · Division · Wt Avg Sold · Wt Avg Turn · Pri Rank · Grade · Tgt Turn · Tgt Sales · Tgt EOM · Vol Grp · WH Stock · SKU Max. Filter Store (autocomplete) + Filter Division.

When the page opens for a (Country, Year, Month) that already has saved output, rows auto-load.

### 5.7 LPM SIM Generate (`/lpm/lpm-sim/generate`)

1. **Filters**: Country (UAE) + Run Date.
2. **Readiness cards** (4): EOM Output for the run month, eligible boxes count, LPM_LocStock SOH coverage, current batch status.
3. **Buttons**: Re-check, **Generate**, **Approve**, **Delete**.
   - **Generate**: writes a Draft `LPMSIM_Batch` + lines. If a Draft already exists for that (Country, RunDate) it overwrites; if an Approved batch exists, the call errors and the user must Delete first.
   - **Approve**: flips the Draft batch to Approved status with timestamp + user.
   - **Delete**: copies batch + lines into the `_Backup` tables (with `BackupTS` and `BackupBy`) inside a transaction, then removes from active tables. Allows a fresh Generate.
4. **Result preview** appears whenever any batch (Draft or Approved) exists for the scope:
   - Metric cards: Lines / Stores / Boxes / Total Qty.
   - Tabs:
     - **Summary (per Store × Division)** — Store ID · Store Name · Division · EOM · SOH · Balance · LPM SIM Qty. Same query as the EOM Summary report tab.
     - **Allocation Lines** — raw `LPMSIM_Output` rows.
   - Filters: Store / Division (Summary) and Store / Item / Box (Lines).
5. The **Summary** tab is the recommended pre-Approve review surface.

### 5.8 LPM SIM Reports (`/lpm/lpm-sim/reports`)

Three tabs over a selected batch (filters: Country + optional Run Date + Batch No):

- **EOM Summary**: per (Store, Division). EOM (TargetEOM) · SOH · Balance · LPM SIM Qty. SOH and SIM Qty are aggregated from `LPM_LocStock` and `LPMSIM_Output` via the same name-based item→division mapping the engine uses.
- **SIM Boxes**: per (Box) or per (Store × Div × Box) with a **"Roll up to Box"** toggle. Box Qty (sum of all items in the box from `whboxitems`), LPM SIM Qty (allocated), PalletType, TypeName, PalletCategory.
- **Item Details**: per (Store × Box × Itemcode). Original Box Item Qty · SKU Max · SOH · LPM Qty.

Filter Store / Filter Division apply client-side over the active tab.

### 5.9 Warehouse Boxes (`/lpm/warehouse-boxes`)

Read-only report from `racks..whboxitems` joined to `bfldata..pallettype`. Filters (autocomplete): Warehouse, Type Name, Pallet Category, LPM. Plus Pallet/Box text search. Capped at 200,000 rows. Columns: Country (literal `'UAE'`) · Warehouse · PalletNo · BoxNo · PalletType · TypeName · PalletCategory · Qty (summed per pallet × box) · LPM. Each Load is logged via `IActionLogger` into `LPMAuditLog` with action `'R'`.

### 5.10 Admin

- `/admin/users` (Admin) — provision Windows users, assign roles, toggle Active, delete.
- `/admin/grades` (Admin or PlanningManager) — Diamond/Platinum/Gold/Silver/Bronze with Share% (count-by-grade) and Markup% (Target Turn calc).
- `/admin/volume-groups` (Admin or PlanningManager) — A/B/C/D/E with Share%.
- `/admin/skumax-rules` (Admin or PlanningManager) — inline-editable matrix per (Country × Division), defaults to "All divisions". Add row inherits previous row's Division + Volume Group + `WHStockFrom = previous.WHStockTo + 1`. Save commits inserts/updates/deletes in a single transaction; saved rows persist on screen.
- `/admin/audit` (Admin) — `LPMAuditLog` browser. Filter by Entity, ChangedBy, date range. Action chip rendered as Insert (green) / Update (blue) / Delete (red) / Read (gray).

---

## 6. EOM Generate Algorithm

Implemented in `LpmSim.Data/Eom/EomCalculator.cs`. Six steps from the original spec.

**Inputs (per run)**: `Country`, `Year1`, `Month1`, plus the configured `LPM_MonthlyWeight`, `LPM_Planned`, `LPM_SalesTurns`, `LPM_WHStock`, `LPM_StoreGrade`, `LPM_VolumeGroup`, `LPM_SKUMaxRule`.

**Step 1 — Priority Ranking** (per Store × Division)

1. Pull 13 periods of `(SoldQty, TurnsQty)` from `LPM_SalesTurns` per store × division — keyed by the `LPM_MonthlyWeight` rows.
2. **Wt Avg Sold Qty** = Σ (`SoldQty_p × Weight_p`).
3. **Wt Avg Turn** = Σ (`TurnsQty_p × Weight_p`).
4. Within each Division for the country:
   - **SoldQtyRank** descending (highest qty = rank 1). Ties share rank.
   - **TurnsRank** ascending (lowest turn = rank 1) — per business rule confirmation.
5. **PriorityRank** = (`SoldQtyRank + TurnsRank`) / 2. Stored as `decimal(9,1)`.

**Step 2 — Target Turn**

1. Sort stores in each division by `PriorityRank` asc, tiebreak `Wt Avg Sold Qty` desc.
2. Allocate grades by `Share%` × store count (rounded). Order Diamond → Bronze (or by `LPM_StoreGrade.SortOrder`).
3. **Target Turn** = `PlannedTurn × (1 − MarkupPct)` for the assigned grade.

**Step 3 — Target Sales** (per Store × Division)

- A = store's Wt Avg Sold Qty.
- B = Σ Wt Avg Sold Qty across all stores in the country for that division.
- **Target Sales** = `(A / B) × PlannedSalesQty`.

**Step 4 — Target EOM**

- Initial EOM = `(TargetTurn × TargetSales) / WeeksInMonth` (integer `DaysInMonth / 7`).
- Sum Initial EOM across all stores in the country for the division → S.
- **Target EOM** = `(InitialEOM / S) × PlannedEOM`.

**Step 5 — Volume Group**

- Sort stores in each division by `Target EOM` desc.
- Allocate Volume Groups by `Share%` × count (Group A first, then B…E).

**Step 6 — SKU Max**

- For each (Store, Division), look up `LPM_SKUMaxRule` matching `(Country, DivCode, GroupCode = VolumeGroup, WHStock IN [WHStockFrom, WHStockTo])` where `WHStock` is the rolled-up WH stock for that (Country, Division) from `LPM_WHStock`.
- Result populates `LPM_EOM_Output.SKUMax`.

**Persistence**: `GenerateAsync` runs DELETE for `(Country, Year1, Month1)` then bulk INSERT new rows in a single SaveChanges. The audit interceptor records both as `'D'` and `'I'` rows in `LPMAuditLog`.

---

## 7. LPM SIM Allocation Algorithm

Implemented in `LpmSim.Data/LpmSim/LpmSimGenerator.cs`. Runs daily (or on demand).

**Inputs**:

1. Eligible boxes: `racks..whboxitems` joined to `bfldata..pallettype` where `PalletCategory='ELIGIBLE'`, `ShopEligible='E'`, `LPMDt IS NOT NULL`, and `(Year(LPMDt) < runYear OR (Year(LPMDt) = runYear AND Month(LPMDt) ≤ runMonth))`.
2. Item → Division mapping: `racks..upc_subclass.MH4ID` → `racks..subclassmaster.MH4ID` → `subclassmaster.Division` (text) **matched by name** to `LPMSIM..Division.Division`. ⚠ `subclassmaster.DivID` (401–421) is NOT used directly because LPMSIM `Division.DivCode` runs 1–21.
3. SOH: sum of `racks..LPM_LocStock.SOH` grouped by (StoreID, Itemcode), excluding null/blank StoreID.
4. EOM rules: `LPMSIM..LPM_EOM_Output` for the run's `(Country, Year, Month)`.

**Validation**: `LPMSIM_Batch` already-Approved for `(Country, RunDate)` blocks the run; user must Delete first. Existing Drafts are overwritten.

**Allocation loop** (per box-item line):

```
for each (BoxNo, LPMDt, ItemCode, Qty) in eligible boxes:
    DivCode = lookup(ItemCode → Division name → DivCode)
    if DivCode is null:    skip (count "items without division mapping")
    
    stores = LPM_EOM_Output rows for (Country, DivCode), sorted by:
              VolumeGroup asc (A → E), PriorityRank asc, WtAvgSoldQty desc
    
    remaining = Qty
    for store in stores:
        if remaining <= 0: break
        sku_balance      = store.SKUMax - SOH(store, ItemCode)         # may be 0 → skip
        target_remaining = store.TargetEOM - already_allocated[(store, DivCode)]
        take = min(remaining, max(0, sku_balance), max(0, target_remaining))
        if take <= 0: continue
        emit LPMSIM_Output(LPMBatchNo, BoxNo, LPMDt, ItemCode, take, store.StoreID)
        already_allocated[(store, DivCode)] += take
        remaining -= take
```

**Output**: rows in `LPMSIM_Output` linked to a Draft `LPMSIM_Batch` (auto-incremented `LPMBatchNo`). Counts (`BoxesProcessed`, `LinesGenerated`, `TotalQty`) updated on the batch row. The batch must be **Approved** to be considered finalized; once Approved it cannot be re-generated until Deleted.

**Per-run reset**: Target EOM consumption resets every run — there is no carry-over across multiple runs in the same month. This matches the explicit business rule: each run starts from zero, and only an Approved batch counts as "the answer for the day".

---

## 8. Audit Logging

### 8.1 Data changes (I/U/D)

`AuditSaveChangesInterceptor` (registered as a scoped EF interceptor on `LpmDbContext`) runs on every `SaveChanges`:

1. Iterates `ChangeTracker.Entries()`.
2. Skips `LpmAuditLog` entries themselves (no recursion).
3. Builds a JSON diff of changed properties (`{ "ColName": { "o": old, "n": new } }`).
4. Writes one `LPMAuditLog` row per tracked entity with `Action ∈ {I, U, D}`, `EntityName`, `EntityKey` (composite PK joined with `|`), `ChangedBy`, `ChangedTS`, `ChangesJson`.

### 8.2 Action / read events ('R')

`IActionLogger` / `ActionLogger` writes `Action='R'` rows for explicit user actions like "Warehouse Boxes Load". Each entry carries the filter parameters and result count in `ChangesJson`. Failures are swallowed so they never break the user action.

### 8.3 Viewing

`/admin/audit` (Admin) — filter by entity name, changed-by, and date range. Last 2,000 rows shown.

---

## 9. Operations

### 9.1 Building & running

```bash
cd "D:\AI Projects\LpmSim"
dotnet build

cd src\LpmSim.Web
dotnet run --launch-profile http
# Listens on http://localhost:5216
```

### 9.2 Database migration

For a fresh install on a new server, run `db/install_new_server.final.sql` (creates `Division`, `DataSettings`, all `LPM_*`, all `LPMSIM_*`, plus seed roles + default grades + default volume groups). Then BCP-out / BCP-in the data tables from the source server using native format:

```bash
bcp "LPMSIM.dbo.<Table>" out <Table>.dat -S <SOURCE> -U LPM -P "..." -n
bcp "LPMSIM.dbo.<Table>" in  <Table>.dat -S <TARGET> -U LPM -P "..." -n -E -b 10000
```

Tables migrated this way: `DataSettings`, `Division`, `LPMDivMax`, `LPMUser`, `LPMUserRole`, `LPM_EOM_Output`, `LPM_SalesTurns`, `LPM_MonthlyWeight`, `LPM_Planned`, `LPM_WHStock`, `LPM_SKUMaxRule`. Seeded data (`LPMRole`, `LPM_StoreGrade`, `LPM_VolumeGroup`) is recreated by the install script.

### 9.3 Hosting (recommended)

**IIS** with the ASP.NET Core 9 Hosting Bundle. App pool: No Managed Code. Site bindings: a friendly internal hostname (e.g., `http://LPMSIM.bflgroup.ae`) once your DNS team adds the A record. Authentication: Windows ON, Anonymous OFF.

**Kestrel as Windows Service** is the simpler alternative when IIS isn't desired.

### 9.4 Connection strings

Two strings are required:
- `ConnectionStrings:Lpm` → LPMSIM database
- `ConnectionStrings:Warehouse` → RACKS database (for cross-DB queries — same server, different DB)

Both default to `BFL-LOGBACKUP\LOGBACKUP` in `appsettings.json`. Override via User Secrets for environments that don't share that file.

---

## 10. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `Login failed for user 'LPM'` | Mixed-mode auth disabled, login missing, default DB inaccessible | Verify SQL Server has Mixed Mode; create login + user in target DB; `ALTER LOGIN [LPM] WITH DEFAULT_DATABASE=[LPMSIM]`. |
| `Cannot open database 'LPMSIM' requested by the login` | LPM has no user mapping in LPMSIM | `USE LPMSIM; CREATE USER [LPM] FOR LOGIN [LPM]; ALTER ROLE db_owner ADD MEMBER [LPM];` |
| LPM SIM Generate produces 0 allocations | `subclassmaster.DivID` doesn't match LPMSIM `Division.DivCode` (different ID spaces), OR items aren't in `upc_subclass` at all | Calculator already maps by Division **name**. Remaining 0s come from missing `upc_subclass` entries — request the missing items be added to `racks..upc_subclass` master data. |
| EOM Generate fails on readiness | One of seven inputs incomplete | Each readiness card explains its specific blocker — fix the upstream input (Weights sum to 100%, Planned has all divisions, Sales/Turns uploaded for the 13 periods, WH Stock present, Grades active, etc.). |
| Excel upload says "Data is Null" | Source data has nullable columns the entity declares non-nullable | All cell reads use the safe `XLCellValue` struct now. If you see this on a new column, declare the matching entity property as nullable (`string?`/`int?`). |
| Browser shows ERR_CONNECTION_REFUSED | App not running | `dotnet run --launch-profile http` from `src\LpmSim.Web`. |

---

## 11. Key business rules captured in code

- **EOM run is per Country, monthly.** Stored output is keyed by `(StoreID, DivCode, Year1, Month1)`; regenerate replaces all rows for the (Country, Year, Month) atomically.
- **TurnsRank is ascending** (low turn = rank 1). PriorityRank = (SoldQtyRank + TurnsRank) / 2, stored as 1 decimal.
- **Grade share rounding**: cumulative integer rounding so the last grade absorbs leftover stores and the total always sums to the store count.
- **Volume Group order**: A → E for allocation priority; A is the highest-priority bucket.
- **Weeks per month** for Initial EOM: integer `DaysInMonth / 7` (matches the spec example of 4 weeks for May).
- **SKU Max** is looked up per `(Country, Division, VolumeGroup, WHStock range)` — completely configurable via `/admin/skumax-rules`.
- **LPM SIM uniqueness**: one batch per (Country, RunDate). Approved batches block re-generation until explicitly Deleted (which archives them to `_Backup`).
- **Division mapping for LPM SIM**: by Division **name** not numeric ID (`subclassmaster.DivID` 401–421 vs LPMSIM `Division.DivCode` 1–21).

---

## 12. Roadmap / deferred

- DNS + IIS publish to `http://LPMSIM.bflgroup.ae` (infra, not code).
- Data quality: ~82% of eligible warehouse items currently have no `racks..upc_subclass` entry, so they can't be mapped to a division. Closing this gap is the single biggest lever for LPM SIM allocation coverage.
- Excel export from LPM SIM Reports (one-click Excel for any of the three tabs).
- Optional: per-run Target EOM accumulation if Planning later wants multi-run consumption tracking.
