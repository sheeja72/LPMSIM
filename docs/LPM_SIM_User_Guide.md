# LPM SIM — Functional User Guide

**For**: Planning team, store-ops admins, anyone using the LPM SIM application.
**Version**: 1.0 — April 2026

---

## 1. What does LPM SIM do?

LPM SIM is the planning team's online tool to decide **which items in the warehouse go to which stores, and in what quantity** — every day and every month.

It replaces the old Excel workflow with a single web app where you:

1. Enter the planning targets for the month (turns, sales, EOM).
2. Compute each store's **monthly EOM plan** — a calculated ceiling per store and per division (Target EOM, SKU Max, Volume Group A–E).
3. Run a **daily allocation** that takes today's eligible warehouse boxes and distributes their items across stores honoring those ceilings.
4. Review the proposed allocation, approve it, and ship.

If the EOM plan answers *"how much can each store hold this month?"*, LPM SIM answers *"given today's pallets and current store stock, who gets what?"*.

---

## 2. Who uses it?

There are four roles. Your administrator gives you exactly one (or more):

| Role | What you can do |
|---|---|
| **Admin** | Everything: provision users, view audit log, plus everything below. |
| **Planning Manager** | Configure planning rules — Store Grades, Volume Groups, SKU Max Rules — plus everything below. |
| **Editor** | Use the planning workflow — enter monthly weights, planned inputs, upload data, run EOM Generate, run LPM SIM Generate, view reports. |
| **Viewer** | Read-only access (no pages bound exclusively yet — placeholder for future). |

If you log in and see "Access denied", show that message to your admin and they'll grant you a role.

---

## 3. Logging in

1. Open **Edge** or **Chrome** on a domain-joined machine and go to the LPM SIM URL provided by IT (e.g., `http://lpmsim.bflgroup.ae`).
2. The app uses your **Windows account automatically** — no username or password to type. You'll see your Windows ID at the top-left of the sidebar (e.g., `BFLDomain\sheeja`).
3. If you get an "Access denied" page, copy the username it shows and ask an admin to add you.

There is no logout button — closing the browser tab is enough.

---

## 4. The sidebar — what each menu does

Once you're in, the dark sidebar on the left has these groups:

### LPM Variables (everyone with Editor / Planning Manager / Admin)

| Menu item | What it's for |
|---|---|
| **Division Max Entry** | One-off legacy entry: max quantity per (store × division). Rarely used now — the EOM Generate process produces the real ceilings. |
| **Monthly Weights** | Tell the system how to weight the last 13 months of sales when computing each store's monthly average. |
| **Planned Inputs** | Enter the Planned Turn, Planned Sales Qty, and Planned EOM **per division per month**. Required input for EOM Generate. |
| **Data Uploads** | Upload two Excel files every month: Sales & Turns history (used for the weighted average), and WH Stock (warehouse-level stock per division). |
| **EOM Generate** | Compute the **monthly** EOM plan: Target Turn / Target Sales / Target EOM / Volume Group / SKU Max for every store × division in the country. Must be run before LPM SIM. |
| **LPM SIM Generate** | The daily allocation. Generates a Draft batch; you review and Approve to finalize. |
| **LPM SIM Reports** | Three views of any past or current allocation batch: EOM Summary, SIM Boxes, Item Details. |
| **Warehouse Boxes** | Browse the eligible UAE warehouse boxes/pallets directly. Useful for sanity-checking what's available. |

### Planning Config (Planning Manager + Admin)

| Menu item | What it's for |
|---|---|
| **Store Grades** | The Diamond / Platinum / Gold / Silver / Bronze table — how the store population is split, and how aggressively each grade gets discounted on Planned Turn. |
| **Volume Groups** | A / B / C / D / E — used to bucket stores after Target EOM is computed and to pick the right SKU Max rule. |
| **SKU Max Rules** | The lookup matrix that says *"if this store falls in Volume Group X, and our country/division WH Stock is between N and M, then SKU Max = K"*. |

### Admin (Admin only)

| Menu item | What it's for |
|---|---|
| **User Access** | Add Windows users, assign roles, deactivate access. |
| **Audit Log** | Every change made by every user — inserts, updates, deletes, and report views. Searchable by user, entity, date range. |

---

## 5. The monthly setup workflow

Do this once each month, before you run EOM Generate.

### Step 1 — Upload Sales & Turns

Menu: **Data Uploads → Sales & Turns** tab.

You'll need an Excel file with these columns (in this order, on the first sheet, row 1 as header):

```
StoreID | Division | Year | Month | SoldQty | Turns
```

- **StoreID** must already exist in the master list. Unknown stores will be flagged.
- **Division** can be the division code (a number) or the division name like "Womenswear".
- **Month** must be 1–12.
- Rows with the same `StoreID + Division + Year + Month` are **replaced** (you can re-upload to fix mistakes).

Click **Download Template** to get a blank Excel with the right headers. Drop your file on the dropzone; the app shows you any error rows with the exact column letter and row number — fix them in Excel and re-upload. When all rows are valid, click **Commit**.

### Step 2 — Upload WH Stock

Menu: **Data Uploads → WH Stock** tab.

Same flow, columns:

```
Country | Division | Year | Month | WHStockQty
```

This is the warehouse total stock per division for the planning month — feeds the SKU Max lookup.

### Step 3 — Set Monthly Weights

Menu: **Monthly Weights**.

- Country defaults to UAE; if you've saved weights before, the latest year/month auto-loads.
- Click **Auto-fill periods** if it's a new month — the system creates 13 rows: the past 12 months + current partial month.
- Or click **Copy prev month** to clone the percentages you used last month.
- Type the weight % into each row (whole numbers, e.g., `10` for 10%). The total must equal **100%** — the metric card at the top turns green when valid.
- **Press Enter** in any weight cell to jump to the next row's weight cell — fast keyboard entry.
- Click **Save** when the total is 100%.

### Step 4 — Enter Planned Inputs

Menu: **Planned Inputs**.

For each (Country, Year, Month) combination:

- Pick the country and month.
- Click **Add all divisions** to create one row per division (or **Copy prev month** to bring forward last month's values).
- For each division, type:
  - **Planned Turn** (decimal, e.g., 8.0)
  - **Planned Sales Qty** (integer)
  - **Planned EOM** (integer)
- **Press Enter** in any cell to walk down the same column row by row, then jump to the next column.
- Click **Save** when done.

The metric cards at the top show the totals so you can sanity-check.

---

## 6. The monthly EOM Generate

Once Steps 1–4 above are done, the EOM plan is one click away.

Menu: **EOM Generate**.

You'll see a panel with **7 readiness cards**, each green or red:

| Card | What it checks |
|---|---|
| Weights | 13 rows for this country/year/month, summing to 100%. |
| Planned | One row per division for this country/year/month. |
| Sales/Turns | Uploaded data covers the 13 weight periods for active stores. |
| WH Stock | One row per division for this country/year/month. |
| Grades | Store Grade table is configured and shares sum to 100%. |
| Volume Groups | Volume Group table is configured and shares sum to 100%. |
| SKU Max Rules | At least one rule exists. |

**Anything red** → click into that page from the sidebar and fix the gap (e.g., upload missing periods, fill in missing planned rows).

When everything is green:

- **View Saved** shows you the last-generated plan for this country/month if there is one.
- **Preview** runs the calculation in memory and shows the proposed plan **without saving**. Use this to sanity-check.
- **Generate** runs the calculation **and writes it to the database**. This is the final step.

After Generate (or View Saved), the result table shows every store × division row with these columns:

| Column | Meaning |
|---|---|
| **Wt Avg Sold** | Weighted average sold qty across the 13 periods for this store × division. |
| **Wt Avg Turn** | Weighted average turn (1 decimal). |
| **Pri Rank** | Priority Rank — the average of (Sold Qty Rank descending, Turn Rank ascending). Lower = higher priority. |
| **Grade** | Diamond / Platinum / Gold / Silver / Bronze, assigned by Priority Rank. |
| **Tgt Turn** | Target Turn — Planned Turn discounted by the grade's markup %. |
| **Tgt Sales** | Target Sales — share of Planned Sales Qty proportional to this store's Wt Avg Sold within the division. |
| **Tgt EOM** | Target EOM — share of Planned EOM, the **monthly stock ceiling** per store × division. |
| **Vol Grp** | A–E, assigned by Target EOM (highest = A). |
| **WH Stock** | Warehouse stock for this country × division (from your upload). |
| **SKU Max** | The per-store-per-division stock limit, looked up from the SKU Max Rules. |

A small chip above the table tells you whether you're looking at **Saved** rows (already in the database, with the timestamp) or a **Live preview** (not yet saved).

The same EOM plan stays in effect for the whole month — you typically generate it once around the 25th–28th and reuse it for daily LPM SIM runs.

---

## 7. The daily LPM SIM Generate

Once the monthly EOM is in place, the daily allocation is the routine task.

Menu: **LPM SIM Generate**.

### What it does

1. Picks up every **eligible warehouse box** for the run month — boxes whose Pallet Category is **ELIGIBLE**, ShopEligible flag is `E`, and LPM date is on or before today.
2. For each item in those boxes, looks up its **division**.
3. For each (store, division) pair, computes how much stock the store still has room for: `SKU Max − current SOH`, capped by `Target EOM − already allocated`.
4. Distributes box items to stores in this priority order:
   - **Volume Group A first**, then B, C, D, E.
   - Within the same group: **Priority Rank ascending**, then **Wt Avg Sold descending**.
5. Writes the resulting allocation rows to a **Draft batch**.

### Step-by-step

1. Pick **Country** (defaults to UAE).
2. Pick **Run Date** (defaults to today).
3. Click **Re-check** to refresh the readiness cards (EOM Output exists, eligible boxes counted, SOH coverage, current batch status).
4. Click **Generate** — the system creates a Draft batch and shows you the result on the same page.

### Reviewing before approval

After Generate, four metric cards appear (Lines, Stores, Boxes, Total Qty), and below them a tabbed panel:

- **Summary (per Store × Division)** — the recommended review surface. Each row: Store ID · Store Name · Division · EOM · SOH · Balance · LPM SIM Qty. Sanity-check that **LPM SIM Qty ≤ Balance** for every row, and totals look right.
- **Allocation Lines** — the raw per-line allocation: which item from which box goes to which store, qty.

Filter by Store / Division / Item / Box to drill into specific cases.

### Approving

Once the Summary looks good:

1. Click **Approve**. The batch flips from Draft to Approved with a timestamp + your username.
2. After Approval, **the run is locked** — Generate is blocked. This is intentional: only one approved run per (Country, Run Date).

### Re-running the same day

If something was off (wrong inputs, you noticed an error after approving):

1. Click **Delete**. The batch and all its lines are moved to backup tables (kept for audit), and the active tables are cleared.
2. Click **Generate** again. A new Draft batch will be created.

You can keep re-Drafting as many times as you want — only the act of **Approve** + **Delete** writes to the audit trail.

### Why a batch might come back empty

A Draft showing 0 lines almost always means one of these:

- **No EOM plan** for the country / month — run EOM Generate first.
- **Items in the eligible boxes have no division mapping** — look at the snackbar after Generate, it tells you how many items were skipped. The fix is in the warehouse data team's hands: missing items need to be added to the master `upc_subclass` list.
- **Every store's `SKU Max − SOH` is zero or negative** — stores already have enough; nothing to allocate.
- **Target EOM is zero** for the affected divisions — check Planned Inputs.

---

## 8. The reports

### LPM SIM Reports (3 tabs)

Menu: **LPM SIM Reports**.

Pick a country, optionally a run date, and a batch from the dropdown (the dropdown shows Draft and Approved batches). All three tabs share the same Store and Division filters.

| Tab | What you see |
|---|---|
| **EOM Summary** | One row per (Store × Division). Columns: EOM (Target EOM), SOH (current stock), Balance (= EOM − SOH), LPM SIM Qty (what we're allocating in this batch). The most useful tab for review-before-approve. |
| **SIM Boxes** | Per box. Box Qty (total inside the box), LPM SIM Qty (how much of it we're shipping), Pallet Type, Type Name, Pallet Category. Use the **"Roll up to Box"** toggle to ignore Store/Div and see one row per box. |
| **Item Details** | Per (Store × Box × Item). Columns include the original box-item Qty, SKU Max, current SOH, and the LPM Qty we're sending. Use this to investigate specific allocations or to understand why a particular store didn't get something. |

### Warehouse Boxes

Menu: **Warehouse Boxes**.

A standalone read-only view of what's currently sitting in the UAE warehouse pallets. Filter by Warehouse, Type Name, Pallet Category, LPM, or text-search a Pallet/Box number. Capped at 200,000 rows. Every load is logged to the Audit Log.

### Division Max Entry

Menu: **Division Max Entry**.

A simple grid for the per-store per-division max quantity. Pick a country to see all stores × divisions. Filter by store or division. Sortable by every column. Click into a Max Qty cell, edit, repeat for as many rows as you want, then click **Save** — only the rows you changed are written.

---

## 9. Configuration (Planning Manager only)

These are usually set once at the start of the year and rarely changed.

### Store Grades

Menu: **Store Grades**.

The five-tier grade table:

| Grade | Default Share % | Default Markup % |
|---|---|---|
| Diamond | 25% | 40% |
| Platinum | 20% | 20% |
| Gold | 20% | 20% |
| Silver | 15% | 10% |
| Bronze | 20% | 10% |

- **Share %** = what fraction of the store population gets this grade (top X% by Priority Rank → Diamond, next Y% → Platinum, …). Total must equal 100%.
- **Markup %** = the discount applied to Planned Turn for that grade. *Target Turn = Planned Turn × (1 − Markup %)*. So Diamond stores get a 40% lower Target Turn than the Planned Turn — meaning they're expected to turn stock faster.

Edit any cell, toggle Active, click **Save**. The footer shows the running Active total.

### Volume Groups

Menu: **Volume Groups**.

A through E with shares (default 25 / 20 / 20 / 15 / 20). Same edit-and-save flow. Used **after** the EOM plan has Target EOMs, to bucket stores: top 25% by Target EOM → Group A, etc.

### SKU Max Rules

Menu: **SKU Max Rules**.

The lookup matrix that turns a store's Volume Group + the country/division warehouse stock into a SKU Max number.

To enter a rule:

1. Pick **Country** (UAE) and **Division** (or "All divisions" to see the whole picture).
2. Click **Add row**. The new row inherits the previous row's Division + Volume Group, and the WH-Stock-From defaults to *previous WH-Stock-To + 1* — so entering a continuous range is just typing the new "To" and SKU Max.
3. Each row says: *"For Volume Group X, when WH Stock is between **From** and **To** (inclusive), the SKU Max for any store in this group is **N**."*
4. Repeat for every (Country × Division × Volume Group × WH Stock band) combination you care about.
5. Click **Save** — the rows with the "Edited" or "New" status badge get committed and the badges flip to "Saved". You can keep adding more rows.

Click the trash icon on a row to mark it for deletion; the next Save removes it.

---

## 10. Common questions

**Q: I clicked Generate but the result is empty. Why?**
A: See section 7 ("Why a batch might come back empty"). Most often it's because items in the eligible boxes don't have a master record in `upc_subclass`, so they can't be mapped to a division. The snackbar after Generate tells you the exact count.

**Q: I want to redo today's allocation — what do I do?**
A: If the batch is still Draft, click **Generate** again — it overwrites. If it's Approved, click **Delete** first (which moves it to backup) then **Generate**.

**Q: My uploaded Excel says "Year (col C) is blank or not a number" on row 47.**
A: Open the Excel, go to row 47, fix the Year cell. Common causes: a stray text value, an empty row in the middle, a date instead of a number. Re-upload — the app will re-validate.

**Q: My division code in Excel doesn't match anything.**
A: The Division column accepts either the **code** (a number) or the **name** ("Womenswear", "Bags", etc.). Names must match the master list exactly. If you typed "Women's Wear" it won't find it.

**Q: I can't see the LPM SIM Reports menu.**
A: You need at least Editor / Planning Manager / Admin role. Ask your admin.

**Q: The screen shows "EOM Output: No rows for UAE 2026/05".**
A: EOM Generate hasn't been run for that month yet. Go to **EOM Generate**, fix any red readiness cards, and click Generate.

**Q: How is "Wt Avg Sold" different from a normal average?**
A: Each of the 13 historical months gets a different weight (you set them in **Monthly Weights**). For example, with 10% on each of the last 12 months and 0% on Jan, the older Januaries don't influence the result. The Wt Avg Sold is the sum of (sold-qty × weight) across all 13 periods. Same idea for Wt Avg Turn.

**Q: What's the difference between SOH and Balance in the Summary?**
A: SOH is the current stock the store holds for that division (sum of all items in that division at that store, taken from the Locstock data). Balance = Target EOM − SOH — the **remaining room** the store can take this month. The LPM SIM allocator never sends more than the Balance.

**Q: The app suddenly stops working in the middle of my session.**
A: Refresh the page (Ctrl+F5). If that doesn't help, contact your IT admin — the server may have restarted.

---

## 11. Glossary

| Term | Definition |
|---|---|
| **EOM** | End-of-Month plan / End-of-Month stock target. The ceiling for how much stock a store should hold for a given division at month end. |
| **Planned Turn** | Planning team's input — expected stock turn for the planning month at division level. Used as the starting point for Target Turn. |
| **Target Turn** | Planned Turn × (1 − Grade Markup %). Each grade has a markup that discounts the planned turn — Diamond stores get the deepest discount because they're expected to turn faster. |
| **Planned Sales Qty** | Planned sales volume per (Country, Division, Month). Distributed to stores in proportion to their Wt Avg Sold Qty. |
| **Target Sales** | A store's share of Planned Sales Qty, weighted by its historical sold qty. |
| **Planned EOM** | Total stock the country wants to hold for that division at month end. Distributed to stores proportional to their Initial EOM. |
| **Target EOM** | The store-level monthly stock ceiling. Computed from Target Turn × Target Sales / weeks-in-month, then proportionally fitted to Planned EOM. |
| **Wt Avg Sold Qty** | Weighted average of the last 13 months of sold quantities, with weights coming from Monthly Weights. |
| **Wt Avg Turn** | Same idea for stock turn. |
| **Sold Qty Rank** | Where this store ranks within its division by Wt Avg Sold Qty (highest = rank 1). |
| **Turns Rank** | Where this store ranks within its division by Wt Avg Turn — **lowest = rank 1** (per business rule). |
| **Priority Rank** | Average of Sold Qty Rank and Turns Rank. Lower = higher priority for allocation. |
| **Grade** | Diamond / Platinum / Gold / Silver / Bronze. Assigned by sorting stores within a division on Priority Rank. |
| **Volume Group** | A / B / C / D / E. Assigned by sorting stores within a division on Target EOM. |
| **SKU Max** | The per-(store, division) ceiling on stock allocation, looked up by Volume Group and country-level WH Stock band. |
| **SOH** | Stock On Hand — the current stock at a store (or summed by division). |
| **Balance** | EOM − SOH. The remaining room a store can take for that division this month. |
| **Pallet Category** | Comes from the master Pallet Type table. Only **ELIGIBLE** pallets are processed by LPM SIM. Other categories (Non Trade, On Hold, Non Eligible) are skipped. |
| **ShopEligible flag** | A column on the warehouse box record. Only `E` boxes are eligible for LPM SIM. |
| **LPMDt** | The date the box was made eligible for LPM. Past or current month is allowed; future-dated boxes are skipped. |
| **Batch** | A single LPM SIM run, identified by an auto-incrementing batch number. Has a Status (Draft or Approved) and a uniquely keyed by (Country, Run Date). |
| **Draft / Approved** | A run starts as Draft (overwritable). Once Approved it's locked; you must Delete (which archives to backup) to redo. |

---

## 12. Quick-reference: monthly cadence

| When | Who | Do this |
|---|---|---|
| 1st–24th of month | Editor | Run **LPM SIM Generate** daily for the live allocation. Approve when reviewed. |
| ~25th–28th | Editor | Upload **Sales & Turns** Excel (covering the latest 13 periods). Upload **WH Stock**. |
| ~25th–28th | Editor | Set **Monthly Weights** for the upcoming month (Auto-fill or Copy prev). |
| ~25th–28th | Editor | Enter **Planned Inputs** for the upcoming month (Add-all-divisions or Copy prev). |
| ~28th–30th | Editor | Run **EOM Generate**. Verify all 7 readiness cards are green, Preview, then Generate. |
| 1st of next month | Editor | Resume daily **LPM SIM Generate** — it will use the new EOM plan automatically. |
| Whenever needed | Planning Manager | Tweak **Store Grades**, **Volume Groups**, or **SKU Max Rules** when policy changes. |
| As needed | Admin | Manage **Users** and review **Audit Log**. |

---

## 13. Where to get help

- **App-level errors / "site can't be reached"** → IT admin.
- **Wrong rule values / unexpected math** → Planning Manager.
- **Missing item data (items not appearing in any division)** → the warehouse data team — they need to add the items to the master `upc_subclass` list.
- **Account / role issues** → your LPM SIM admin (the person who provisioned you).

---

*End of guide.*
