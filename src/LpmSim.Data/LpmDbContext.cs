using LpmSim.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data;

public class LpmDbContext(DbContextOptions<LpmDbContext> options) : DbContext(options)
{
    public DbSet<Division> Divisions => Set<Division>();
    public DbSet<DataSetting> DataSettings => Set<DataSetting>();
    public DbSet<LpmDivMax> LpmDivMaxes => Set<LpmDivMax>();

    public DbSet<LpmUser> LpmUsers => Set<LpmUser>();
    public DbSet<LpmRole> LpmRoles => Set<LpmRole>();
    public DbSet<LpmUserRole> LpmUserRoles => Set<LpmUserRole>();
    public DbSet<LpmAuditLog> LpmAuditLogs => Set<LpmAuditLog>();

    public DbSet<LpmEomOutput> LpmEomOutputs => Set<LpmEomOutput>();
    public DbSet<LpmSimBatch> LpmSimBatches => Set<LpmSimBatch>();
    public DbSet<LpmSimOutput> LpmSimOutputs => Set<LpmSimOutput>();
    public DbSet<LpmSimAllocTrace> LpmSimAllocTraces => Set<LpmSimAllocTrace>();
    public DbSet<LpmSimStoreItemBalance> LpmSimStoreItemBalances => Set<LpmSimStoreItemBalance>();
    public DbSet<LpmSimStoreDivBalance>  LpmSimStoreDivBalances  => Set<LpmSimStoreDivBalance>();
    public DbSet<LpmSimBoxBalance>       LpmSimBoxBalances       => Set<LpmSimBoxBalance>();
    public DbSet<LpmSalesTurns> LpmSalesTurns => Set<LpmSalesTurns>();
    public DbSet<LpmMonthlyWeight> LpmMonthlyWeights => Set<LpmMonthlyWeight>();
    public DbSet<LpmPlanned> LpmPlanneds => Set<LpmPlanned>();
    public DbSet<LpmStoreGrade> LpmStoreGrades => Set<LpmStoreGrade>();
    public DbSet<LpmVolumeGroup> LpmVolumeGroups => Set<LpmVolumeGroup>();
    public DbSet<LpmWHStock> LpmWHStocks => Set<LpmWHStock>();
    public DbSet<LpmSKUMaxRule> LpmSKUMaxRules => Set<LpmSKUMaxRule>();
    public DbSet<LpmStoreDivAccess> LpmStoreDivAccesses => Set<LpmStoreDivAccess>();
    public DbSet<LpmWarehousePriority> LpmWarehousePriorities => Set<LpmWarehousePriority>();
    public DbSet<LpmWeeklySalesTargetSplit> LpmWeeklySalesTargetSplits => Set<LpmWeeklySalesTargetSplit>();
    public DbSet<LpmStoreDeptAccess> LpmStoreDeptAccesses => Set<LpmStoreDeptAccess>();
    public DbSet<LpmStoreCapacity> LpmStoreCapacities => Set<LpmStoreCapacity>();
    // 1.14.77 — Parent/Child country linkage (UAE -> OMAN today). See LpmCountryLink.
    public DbSet<LpmCountryLink>  LpmCountryLinks   => Set<LpmCountryLink>();
    // 1.14.102 — Per-country Build SKU Max lock (row presence = locked).
    public DbSet<LpmSkuMaxLock>   LpmSkuMaxLocks    => Set<LpmSkuMaxLock>();
    public DbSet<LpmSimItemSkuMax> LpmSimItemSkuMaxes => Set<LpmSimItemSkuMax>();
    public DbSet<LpmSimProductionSchedule> LpmSimProductionSchedules => Set<LpmSimProductionSchedule>();
    public DbSet<LpmSimAdmRun>     LpmSimAdmRuns      => Set<LpmSimAdmRun>();
    public DbSet<LpmSimAdmBoxAlloc> LpmSimAdmBoxAllocs => Set<LpmSimAdmBoxAlloc>();
    // 1.14.26 — per-eligible-box gap diagnostic, written at the end of every
    // successful SIM Generate. Table created by migration 046.
    public DbSet<LpmSimUnallocatedDiagnostic> LpmSimUnallocatedDiagnostics => Set<LpmSimUnallocatedDiagnostic>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Division>(e =>
        {
            e.ToTable("Division");
            e.HasKey(x => x.DivCode);
            e.Property(x => x.Name).HasColumnName("Division").HasMaxLength(50);
            // 1.14.55 — Soft-delete flag. NOT NULL with DB-side default of 1
            // (existing rows stay active after migration 055).
            e.Property(x => x.IsActive).HasDefaultValue(true);
        });

        mb.Entity<DataSetting>(e =>
        {
            // dbo.DataSettings in LPMSIM is a SYNONYM that points to
            // bfldata.dbo.DataSettings (see migration 020). EF doesn't need
            // to know — synonyms are transparent to SqlClient.
            e.ToTable("DataSettings");
            e.HasNoKey();
            e.Property(x => x.StoreID).HasColumnName("StoreID").HasMaxLength(25);
            e.Property(x => x.PBFullname).HasColumnName("PBFullname").HasMaxLength(200);
            e.Property(x => x.Country).HasColumnName("Country").HasMaxLength(20);
            e.Property(x => x.SIMCountry).HasColumnName("SIMCountry").HasMaxLength(20);
            e.Property(x => x.ActiveStore).HasColumnName("ActiveStore").HasMaxLength(1);
        });

        mb.Entity<LpmDivMax>(e =>
        {
            e.ToTable("LPMDivMax");
            e.HasKey(x => new { x.StoreID, x.DivCode });
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.UserID).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmUser>(e =>
        {
            e.ToTable("LPMUser");
            e.HasKey(x => x.Username);
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.HasMany(x => x.UserRoles)
                .WithOne(ur => ur.User)
                .HasForeignKey(ur => ur.Username)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<LpmRole>(e =>
        {
            e.ToTable("LPMRole");
            e.HasKey(x => x.RoleCode);
            e.Property(x => x.RoleCode).HasMaxLength(20);
            e.Property(x => x.RoleName).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmUserRole>(e =>
        {
            e.ToTable("LPMUserRole");
            e.HasKey(x => new { x.Username, x.RoleCode });
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.RoleCode).HasMaxLength(20);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.HasOne(x => x.Role)
                .WithMany()
                .HasForeignKey(x => x.RoleCode)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<LpmAuditLog>(e =>
        {
            e.ToTable("LPMAuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityName).HasMaxLength(100);
            e.Property(x => x.EntityKey).HasMaxLength(200);
            e.Property(x => x.Action).HasColumnType("char(1)");
            e.Property(x => x.ChangedBy).HasMaxLength(100);
            e.Property(x => x.ChangedTS).HasColumnType("datetime2(0)");
            e.Property(x => x.ChangesJson).HasColumnType("nvarchar(max)");
        });

        mb.Entity<LpmEomOutput>(e =>
        {
            e.ToTable("LPM_EOM_Output");
            e.HasKey(x => new { x.StoreID, x.DivCode, x.Year1, x.Month1 });
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.WtAvgSoldQty).HasPrecision(18, 4);
            e.Property(x => x.WtAvgTurn).HasPrecision(18, 4);
            e.Property(x => x.PriorityRank).HasPrecision(9, 1);
            e.Property(x => x.TargetTurn).HasPrecision(18, 4);
            e.Property(x => x.TargetSales).HasPrecision(18, 2);
            e.Property(x => x.TargetEOM).HasPrecision(18, 2);
            // 1.14.53 — Ini.EOM + Pre-Store-Cap EOM (new EOM decomposition cols).
            e.Property(x => x.IniEom).HasPrecision(18, 2);
            e.Property(x => x.PreStoreCapEom).HasPrecision(18, 2);
            e.Property(x => x.VolumeGroup).HasMaxLength(10);
            e.Property(x => x.Grade).HasMaxLength(20);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmSalesTurns>(e =>
        {
            e.ToTable("LPM_SalesTurns");
            e.HasKey(x => new { x.StoreID, x.DivCode, x.Year1, x.Month1 });
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.SoldQty).HasPrecision(18, 4);
            e.Property(x => x.TurnsQty).HasPrecision(18, 4);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmMonthlyWeight>(e =>
        {
            e.ToTable("LPM_MonthlyWeight");
            e.HasKey(x => new { x.Country, x.RunYear, x.RunMonth, x.PeriodSeq });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.WeightPct).HasPrecision(6, 4);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmPlanned>(e =>
        {
            e.ToTable("LPM_Planned");
            e.HasKey(x => new { x.Country, x.DivCode, x.Year1, x.Month1 });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.UserID).HasMaxLength(100);
            e.Property(x => x.PlannedTurn).HasPrecision(18, 4);
            e.Property(x => x.PlannedSalesQty).HasPrecision(18, 4);
            e.Property(x => x.PlannedEOM).HasPrecision(18, 4);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmStoreGrade>(e =>
        {
            e.ToTable("LPM_StoreGrade");
            e.HasKey(x => new { x.Country, x.GradeCode });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.GradeCode).HasMaxLength(20);
            e.Property(x => x.GradeName).HasMaxLength(100);
            e.Property(x => x.SharePct).HasPrecision(6, 4);
            e.Property(x => x.MarkupPct).HasPrecision(6, 4);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmVolumeGroup>(e =>
        {
            e.ToTable("LPM_VolumeGroup");
            // 1.14.39 — PK widened to (Country, DivCode, GroupCode) so each
            // Division can carry its own bucket distribution. Old PK was
            // (Country, GroupCode). Migration 051 handles the DB-side swap.
            e.HasKey(x => new { x.Country, x.DivCode, x.GroupCode });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.GroupCode).HasMaxLength(20);
            e.Property(x => x.GroupName).HasMaxLength(100);
            e.Property(x => x.SharePct).HasPrecision(6, 4);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmWHStock>(e =>
        {
            e.ToTable("LPM_WHStock");
            e.HasKey(x => new { x.Country, x.DivCode, x.Year1, x.Month1 });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.UserID).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmSimItemSkuMax>(e =>
        {
            e.ToTable("LPM_SimItemSkuMax");
            e.HasKey(x => new { x.Country, x.Year1, x.Month1, x.StoreID, x.ItemCode, x.Season });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.ItemCode).HasMaxLength(30);
            e.Property(x => x.Season).HasMaxLength(1).IsFixedLength();
            e.Property(x => x.VolumeGroup).HasMaxLength(20);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmStoreDivAccess>(e =>
        {
            e.ToTable("LPM_StoreDivAccess");
            e.HasKey(x => new { x.Country, x.StoreID, x.DivCode });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmWarehousePriority>(e =>
        {
            e.ToTable("LPM_WarehousePriority");
            e.HasKey(x => new { x.Country, x.Warehouse });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.Warehouse).HasMaxLength(50);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmWeeklySalesTargetSplit>(e =>
        {
            e.ToTable("LPM_WeeklySalesTargetSplit");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Country, x.Year1, x.Month1, x.DivCode, x.WeekNo })
             .IsUnique();
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.SplitPct).HasPrecision(7, 4);
            e.Property(x => x.CreateBy).HasMaxLength(100);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmStoreDeptAccess>(e =>
        {
            e.ToTable("LPM_StoreDeptAccess");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Country, x.StoreID, x.DivCode, x.Department })
             .IsUnique();
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.Department).HasMaxLength(60);
            e.Property(x => x.DeptPct).HasPrecision(7, 4);
            e.Property(x => x.Remarks).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        // 1.14.51 — per-store EOM capacity (Planning Config → Stores Capacity EOM).
        mb.Entity<LpmStoreCapacity>(e =>
        {
            e.ToTable("LPM_StoreCapacity");
            e.HasKey(x => new { x.Country, x.StoreID });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        // 1.14.77 — Parent/Child country linkage. Used by EOM Calculator
        // (Child uses Parent's WH source) and SIM Generate (Parent includes
        // Child's stores). See LpmCountryLink + db/057_lpm_country_link.sql.
        mb.Entity<LpmCountryLink>(e =>
        {
            e.ToTable("LPM_CountryLink");
            e.HasKey(x => new { x.ParentCountry, x.ChildCountry });
            e.Property(x => x.ParentCountry).HasMaxLength(20);
            e.Property(x => x.ChildCountry ).HasMaxLength(20);
            e.Property(x => x.CreatedBy    ).HasMaxLength(100);
            e.Property(x => x.CreateTS     ).HasColumnType("datetime2(0)");
        });

        // 1.14.102 — Per-country Build SKU Max lock. Row presence = locked.
        // Both the manual SkuMaxBuildJobManager.Start path and the nightly
        // SkuMaxBuildScheduler check this table and block / skip the country
        // when a row exists. See db/060_lpm_skumaxlock.sql.
        mb.Entity<LpmSkuMaxLock>(e =>
        {
            e.ToTable("LPM_SkuMaxLock");
            e.HasKey(x => x.Country);
            e.Property(x => x.Country ).HasMaxLength(50);
            e.Property(x => x.LockedBy).HasMaxLength(100);
            e.Property(x => x.Reason  ).HasMaxLength(500);
            e.Property(x => x.LockedAt).HasColumnType("datetime");
        });

        mb.Entity<LpmSKUMaxRule>(e =>
        {
            e.ToTable("LPM_SKUMaxRule");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.GroupCode).HasMaxLength(20);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            // 1.14.40 hotfix — FK widened to 3-column composite to match
            // the new LpmVolumeGroup PK (Country, DivCode, GroupCode) from
            // 1.14.39. The old 2-column FK config caused EF Core's model
            // build to fail at startup (PK has 3 cols, FK has 2 → mismatch),
            // which surfaced as HTTP 500 on EVERY page that touched the
            // DbContext — including SIM Generate.
            //
            // LpmSKUMaxRule already has all 3 columns, so this matches
            // cleanly. The corresponding DB-side FK was already dropped
            // by migration 051 (Volume Groups upload validation now does
            // the (Country, DivCode, GroupCode) check at the app layer).
            e.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => new { x.Country, x.DivCode, x.GroupCode })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Division)
                .WithMany()
                .HasForeignKey(x => x.DivCode)
                .OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<LpmSimBatch>(e =>
        {
            e.ToTable("LPMSIM_Batch");
            e.HasKey(x => x.LPMBatchNo);
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.ApprovedBy).HasMaxLength(100);
            e.Property(x => x.RunDate).HasColumnType("date");
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.ApprovedTS).HasColumnType("datetime2(0)");
            e.Property(x => x.Sources).HasMaxLength(20);
            e.Property(x => x.Seasons).HasMaxLength(20);
            e.Property(x => x.Warehouses).HasMaxLength(200);
            e.Property(x => x.FillStrategy).HasMaxLength(40);
        });

        mb.Entity<LpmSimOutput>(e =>
        {
            e.ToTable("LPMSIM_Output");
            e.HasKey(x => x.Id);
            e.Property(x => x.BoxNo).HasMaxLength(25);
            e.Property(x => x.Itemcode).HasMaxLength(30);
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.LPMDt).HasColumnType("date");
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.Phase).HasMaxLength(8).HasDefaultValue("P1");
            e.Property(x => x.IsRoundRobin).HasDefaultValue(false);
        });

        mb.Entity<LpmSimAllocTrace>(e =>
        {
            e.ToTable("LPMSIM_AllocTrace");
            e.HasKey(x => x.Id);
            e.Property(x => x.BoxNo).HasMaxLength(25);
            e.Property(x => x.ItemCode).HasMaxLength(30);
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.Decision).HasMaxLength(20);
            e.Property(x => x.Phase).HasMaxLength(8).HasDefaultValue("P1");
            e.Property(x => x.TargetEOM).HasColumnType("decimal(18,4)");
            e.Property(x => x.AlreadyAllocated).HasColumnType("decimal(18,4)");
            e.Property(x => x.TargetRemain).HasColumnType("decimal(18,4)");
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmSimStoreItemBalance>(e =>
        {
            e.ToTable("LPMSIM_StoreItemBalance");
            e.HasKey(x => new { x.LPMBatchNo, x.StoreID, x.ItemCode });
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.ItemCode).HasMaxLength(30);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmSimStoreDivBalance>(e =>
        {
            e.ToTable("LPMSIM_StoreDivBalance");
            e.HasKey(x => new { x.LPMBatchNo, x.StoreID, x.DivCode });
            e.Property(x => x.StoreID).HasMaxLength(25);
            e.Property(x => x.TargetEOM).HasColumnType("decimal(18,4)");
            e.Property(x => x.DivBalanceRemaining).HasColumnType("decimal(18,4)");
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            // Persisted computed columns from migration 018. Tell EF not to
            // write them on INSERT/UPDATE — SQL Server computes them.
            e.Property(x => x.EomBalance)
             .HasColumnType("decimal(18,4)")
             .ValueGeneratedOnAddOrUpdate()
             .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            e.Property(x => x.FillRate)
             .HasColumnType("decimal(18,4)")
             .ValueGeneratedOnAddOrUpdate()
             .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
        });

        mb.Entity<LpmSimBoxBalance>(e =>
        {
            e.ToTable("LPMSIM_BoxBalance");
            e.HasKey(x => new { x.LPMBatchNo, x.BoxNo });
            e.Property(x => x.BoxNo).HasMaxLength(25);
            e.Property(x => x.BoxKind).HasMaxLength(8);
            e.Property(x => x.LPMDt).HasColumnType("date");
            e.Property(x => x.UsabilityPct).HasColumnType("decimal(6,1)");
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmSimProductionSchedule>(e =>
        {
            e.ToTable("LPMSIM_ProductionSchedule");
            e.HasKey(x => x.LPMBatchNo);
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Draft");
            e.Property(x => x.MinUsabilityPct).HasPrecision(5, 2);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.ApprovedBy).HasMaxLength(100);
            e.Property(x => x.PriorityWarehouses).HasMaxLength(500);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.ApprovedTS).HasColumnType("datetime2(0)");
            e.HasOne<LpmSimBatch>()
                .WithOne()
                .HasForeignKey<LpmSimProductionSchedule>(x => x.LPMBatchNo)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<LpmSimAdmRun>(e =>
        {
            e.ToTable("LPMSIM_AdmRun");
            e.HasKey(x => x.AdmRunNo);
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Draft");
            e.Property(x => x.RunDate).HasColumnType("date");
            e.Property(x => x.Week1TargetPct).HasPrecision(5, 2);
            e.Property(x => x.BrandCapPct).HasPrecision(5, 2);
            e.Property(x => x.CreatedBy).HasMaxLength(80);
            e.Property(x => x.ApprovedBy).HasMaxLength(80);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.ApprovedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<LpmSimAdmBoxAlloc>(e =>
        {
            e.ToTable("LPMSIM_AdmBoxAlloc");
            e.HasKey(x => x.Id);
            e.Property(x => x.BoxNo).HasMaxLength(50);
            e.Property(x => x.Warehouse).HasMaxLength(40);
            e.Property(x => x.LPMBrand).HasMaxLength(80);
            e.Property(x => x.Division).HasMaxLength(80);
            e.Property(x => x.LPMDt).HasColumnType("date");
            e.Property(x => x.DivFillRatePct).HasPrecision(5, 2);
            e.Property(x => x.DivFillGapPct).HasPrecision(5, 2);
            e.Property(x => x.Reason).HasMaxLength(40);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.HasOne<LpmSimAdmRun>()
                .WithMany()
                .HasForeignKey(x => x.AdmRunNo)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 1.14.26 — Per-eligible-box allocation gap diagnostic. Table created
        // by migration 046. RemainingQty is a PERSISTED computed column on
        // the DB side, so we mark it ValueGeneratedOnAddOrUpdate +
        // PropertySaveBehavior.Ignore so EF reads it but never tries to
        // INSERT or UPDATE the value (same pattern as the EOM fill-rate
        // computed columns above).
        mb.Entity<LpmSimUnallocatedDiagnostic>(e =>
        {
            e.ToTable("LPMSIM_UnallocatedDiagnostic");
            e.HasKey(x => new { x.LPMBatchNo, x.BoxNo });
            e.Property(x => x.BoxNo).HasMaxLength(25);
            e.Property(x => x.PalletNo).HasMaxLength(50);
            e.Property(x => x.LPMDt).HasColumnType("date");
            e.Property(x => x.BoxKind).HasMaxLength(10);
            e.Property(x => x.TopReason).HasMaxLength(50);
            e.Property(x => x.Reasons).HasMaxLength(400);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.Property(x => x.RemainingQty)
             .ValueGeneratedOnAddOrUpdate()
             .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            e.HasOne<LpmSimBatch>()
                .WithMany()
                .HasForeignKey(x => x.LPMBatchNo)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
