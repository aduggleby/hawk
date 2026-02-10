using Hawk.Web.Services.Email;

namespace Hawk.Web.Services.Email;

/// <summary>
/// Adapter that lets ASP.NET Core Identity UI email flows (register/forgot password/etc.)
/// reuse the app's Resend-compatible sender.
/// </summary>
public sealed class IdentityUiEmailSender(
    IEmailSender emailSender,
    IConfiguration config,
    ILogger<IdentityUiEmailSender> logger)
    : Microsoft.AspNetCore.Identity.UI.Services.IEmailSender
{
    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var enabled = config.GetValue("Hawk:Email:Enabled", true);
        if (!enabled)
        {
            logger.LogWarning("Identity email suppressed because Hawk:Email:Enabled=false (to={To}, subject={Subject})", email, subject);
            throw new InvalidOperationException("Email is disabled on this server (Hawk:Email:Enabled=false).");
        }

        var from = config["Hawk:Email:From"] ?? config["Hawk:Resend:From"];
        if (string.IsNullOrWhiteSpace(from))
        {
            logger.LogError("Identity email failed because From address is not configured (Hawk:Email:From or Hawk:Resend:From). to={To}, subject={Subject}", email, subject);
            throw new InvalidOperationException("Email From address is not configured (Hawk:Email:From).");
        }

        try
        {
            await emailSender.SendAsync(from, [email], subject, htmlMessage, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Don't log the HTML body; it can contain sensitive tokens.
            logger.LogError(ex, "Identity email send failed (to={To}, subject={Subject})", email, subject);
            throw;
        }
    }
}

