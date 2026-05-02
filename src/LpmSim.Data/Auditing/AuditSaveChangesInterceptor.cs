using System.Text.Json;
using LpmSim.Core;
using LpmSim.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LpmSim.Data.Auditing;

public class AuditSaveChangesInterceptor(ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        AppendAuditRows(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        AppendAuditRows(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void AppendAuditRows(DbContext? ctx)
    {
        if (ctx is null) return;

        var user = currentUser.Name;
        var now = DateTime.Now;

        var logs = new List<LpmAuditLog>();
        foreach (var entry in ctx.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is LpmAuditLog) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var entityName = entry.Metadata.GetTableName() ?? entry.Entity.GetType().Name;
            var pk = entry.Metadata.FindPrimaryKey();
            var key = pk is null
                ? ""
                : string.Join("|", pk.Properties.Select(p =>
                    (entry.State == EntityState.Deleted
                        ? entry.OriginalValues[p]
                        : entry.CurrentValues[p])?.ToString() ?? ""));

            var action = entry.State switch
            {
                EntityState.Added    => 'I',
                EntityState.Modified => 'U',
                EntityState.Deleted  => 'D',
                _                    => '?'
            };

            var changes = BuildChangeMap(entry);

            logs.Add(new LpmAuditLog
            {
                EntityName  = entityName,
                EntityKey   = Truncate(key, 200),
                Action      = action,
                ChangedBy   = Truncate(user, 100),
                ChangedTS   = now,
                ChangesJson = JsonSerializer.Serialize(changes),
            });
        }

        if (logs.Count > 0)
            ctx.Set<LpmAuditLog>().AddRange(logs);
    }

    private static Dictionary<string, object?> BuildChangeMap(EntityEntry entry)
    {
        var map = new Dictionary<string, object?>();
        foreach (var prop in entry.Properties)
        {
            var name = prop.Metadata.Name;
            switch (entry.State)
            {
                case EntityState.Added:
                    map[name] = new { n = prop.CurrentValue };
                    break;
                case EntityState.Deleted:
                    map[name] = new { o = prop.OriginalValue };
                    break;
                case EntityState.Modified:
                    if (prop.IsModified)
                        map[name] = new { o = prop.OriginalValue, n = prop.CurrentValue };
                    break;
            }
        }
        return map;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
