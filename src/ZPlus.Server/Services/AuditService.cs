using ZPlus.Server.Data;
using ZPlus.Server.Models;

namespace ZPlus.Server.Services;

/// <summary>Writes append-only audit records for security- and admin-relevant actions.</summary>
public class AuditService(AppDbContext db)
{
    /// <summary>
    /// Records an action. Saves immediately so the entry survives even if the surrounding
    /// request fails afterward. Never throws — auditing must not break the operation it logs.
    /// </summary>
    public async Task LogAsync(Guid? actorUserId, string actorEmail, string action, string details = "")
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                WhenUtc = DateTime.UtcNow,
                ActorUserId = actorUserId,
                ActorEmail = actorEmail,
                Action = action,
                Details = details,
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // Auditing is best-effort; swallow so it can't fail the caller.
        }
    }
}
