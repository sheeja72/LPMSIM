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
    public DbSet<LpmSimItemSkuMax> LpmSimItemSkuMaxes => Set<LpmSimItemSkuMax>();
    public DbSet<LpmSimProductionSchedule> LpmSimProductionSchedules => Set<LpmSimProductionSchedule>();
    public DbSet<LpmSimAdmRun>     LpmSimAdmRuns      => Set<LpmSimAdmRun>();
    public DbSet<LpmSimAdmBoxAlloc> LpmSimAdmBoxAllocs => Set<LpmSimAdmBoxAlloc>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Division>(e =>
        {
            e.ToTable("Division");
            e.HasKey(x => x.DivCode);
            e.Property(x => x.Name).HasColumnName("Division").HasMaxLength(50);
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
            e.HasKey(x => new { x.Country, x.GroupCode });
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

        mb.Entity<LpmSKUMaxRule>(e =>
        {
            e.ToTable("LPM_SKUMaxRule");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.GroupCode).HasMaxLength(20);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            // FK is now composite (Country, GroupCode) since LPM_VolumeGroup is per-country.
            e.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => new { x.Country, x.GroupCode })
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
    }
}
