using System.Text.Json;
using LpmSim.Core;
using LpmSim.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.Auditing;

/// <summary>
/// Records user actions (reports opened, filters applied, etc.) into LPMAuditLog
/// using Action='R' (Read). Data-change audits (I/U/D) continue to flow through
/// the EF SaveChangesInterceptor automatically.
/// </summary>
public interface IActionLogger
{
    Task LogAsync(string entity, string key, object? details = null, CancellationToken ct = default);
}

public class ActionLogger(IDbContextFactory<LpmDbContext> dbFactory, ICurrentUser currentUser) : IActionLogger
{
    public async Task LogAsync(string entity, string key, object? details = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.LpmAuditLogs.Add(new LpmAuditLog
            {
                EntityName  = entity,
                EntityKey   = key.Length > 200 ? key[..200] : key,
                Action      = 'R',
                ChangedBy   = currentUser.Name,
                ChangedTS   = DateTime.Now,
                ChangesJson = details is null ? null : JsonSerializer.Serialize(details),
            });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Audit must never break the user action. Swallow and move on.
        }
    }
}
