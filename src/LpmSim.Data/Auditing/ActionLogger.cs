using System.Text.Json;
using LpmSim.Core;
using LpmSim.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace LpmSim.Data.Auditing;

/// <summary>
/// Records user actions into LPMAuditLog. Three Action codes are used:
/// <list type="bullet">
///   <item><c>'R'</c> — Read (report loaded, filter applied). Default for
///     <see cref="LogAsync"/>. Pre-1.14.22 the only code this class wrote.</item>
///   <item><c>'X'</c> — eXecute (a button click that initiated a business
///     action — Generate, Approve, Delete, Build SKU Max, Save, Upload, etc.
///     Added 1.14.22 so action-button events are filterable separately from
///     read/view events).</item>
///   <item><c>'I'/'U'/'D'</c> — Insert / Update / Delete on an EF-tracked
///     entity. Written automatically by the SaveChangesInterceptor — callers
///     never set these explicitly.</item>
/// </list>
/// </summary>
public interface IActionLogger
{
    /// <summary>
    /// Write one audit row. <paramref name="action"/> defaults to <c>'R'</c>
    /// so existing 3-argument callers (Read events) keep their previous
    /// behaviour. Pass <c>action: 'X'</c> from button-click handlers.
    /// </summary>
    Task LogAsync(string entity, string key, object? details = null, char action = 'R', CancellationToken ct = default);
}

public class ActionLogger(IDbContextFactory<LpmDbContext> dbFactory, ICurrentUser currentUser) : IActionLogger
{
    public async Task LogAsync(string entity, string key, object? details = null, char action = 'R', CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.LpmAuditLogs.Add(new LpmAuditLog
            {
                EntityName  = entity,
                EntityKey   = key.Length > 200 ? key[..200] : key,
                Action      = action,
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
