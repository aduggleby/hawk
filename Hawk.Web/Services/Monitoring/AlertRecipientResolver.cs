// <file>
// <summary>
// Resolves who receives alert emails for a monitor, and which rule picked them.
// </summary>
// </file>

using Hawk.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MonitorEntity = Hawk.Web.Data.Monitoring.Monitor;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Where the alert recipients came from.
/// </summary>
public enum AlertRecipientSource
{
    /// <summary>Per-monitor alert email override.</summary>
    Monitor,

    /// <summary>The owner's account-wide alert email.</summary>
    Account,

    /// <summary>The owner's Identity email.</summary>
    Owner,

    /// <summary>All Admin users (fallback when the monitor has no usable owner).</summary>
    Admins,
}

/// <summary>
/// Resolved alert recipients and their source.
/// </summary>
public sealed record AlertRecipients(AlertRecipientSource Source, string[] Addresses);

/// <summary>
/// Resolves alert recipients in order: monitor override, owner's account override, owner's email, all admins.
/// </summary>
public sealed class AlertRecipientResolver(ApplicationDbContext db, UserManager<IdentityUser> userManager)
{
    public async Task<AlertRecipients> ResolveAsync(MonitorEntity monitor, CancellationToken cancellationToken)
    {
        // 1) Per-monitor override.
        if (!string.IsNullOrWhiteSpace(monitor.AlertEmailOverride))
            return new AlertRecipients(AlertRecipientSource.Monitor, [monitor.AlertEmailOverride.Trim()]);

        // 2) Account-wide override for the monitor owner.
        if (!string.IsNullOrWhiteSpace(monitor.CreatedByUserId))
        {
            var accountOverride = await db.UserAlertSettings
                .Where(x => x.UserId == monitor.CreatedByUserId)
                .Select(x => x.AlertEmail)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(accountOverride))
                return new AlertRecipients(AlertRecipientSource.Account, [accountOverride.Trim()]);

            // 3) Owner's Identity email.
            var owner = await userManager.FindByIdAsync(monitor.CreatedByUserId);
            if (!string.IsNullOrWhiteSpace(owner?.Email))
                return new AlertRecipients(AlertRecipientSource.Owner, [owner!.Email!]);
        }

        // Fallback: previous v1 behavior, email all Admin users.
        var admins = await userManager.GetUsersInRoleAsync("Admin");
        return new AlertRecipients(
            AlertRecipientSource.Admins,
            admins
                .Select(a => a.Email)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(e => e!)
                .ToArray());
    }
}
