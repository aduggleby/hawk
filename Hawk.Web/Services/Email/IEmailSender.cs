// <file>
// <summary>
// Email abstraction used for alerting on monitor failures.
// Implemented via a Resend-compatible HTTP API.
// </summary>
// </file>

namespace Hawk.Web.Services.Email;

/// <summary>
/// Sends email messages.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends an email message.
    /// </summary>
    /// <param name="from">From address.</param>
    /// <param name="to">To addresses.</param>
    /// <param name="subject">Subject line.</param>
    /// <param name="html">HTML body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(string from, IReadOnlyList<string> to, string subject, string html, CancellationToken cancellationToken);
}

