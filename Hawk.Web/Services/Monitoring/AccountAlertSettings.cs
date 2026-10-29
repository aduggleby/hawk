// <file>
// <summary>
// Writes the account-wide alert email override (UserAlertSettings).
// </summary>
// </file>

using Hawk.Web.Data;
using Hawk.Web.Data.Alerting;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Reads and writes a user's account-wide alert email. It applies only to monitors the user owns.
/// </summary>
public sealed class AccountAlertSettings(ApplicationDbContext db)
{
    /// <summary>
    /// Returns the user's alert email override, or null.
    /// </summary>
    public Task<string?> GetEmailAsync(string userId, CancellationToken cancellationToken) =>
        db.UserAlertSettings
            .Where(x => x.UserId == userId)
            .Select(x => x.AlertEmail)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Sets the alert email (trimmed), or removes the override when <paramref name="email"/> is null or blank.
    /// Saves immediately. The caller validates the value with <see cref="AlertSettingsValidator"/>.
    /// </summary>
    public async Task SetEmailAsync(string userId, string? email, CancellationToken cancellationToken)
    {
        await StageEmailAsync(userId, email, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Stages the same change without saving, for pages that save several settings together.
    /// </summary>
    public async Task StageEmailAsync(string userId, string? email, CancellationToken cancellationToken)
    {
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

        var settings = await db.UserAlertSettings.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (email is null)
        {
            if (settings is not null)
                db.UserAlertSettings.Remove(settings);
        }
        else if (settings is null)
        {
            db.UserAlertSettings.Add(new UserAlertSettings
            {
                UserId = userId,
                AlertEmail = email,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            settings.AlertEmail = email;
            settings.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
