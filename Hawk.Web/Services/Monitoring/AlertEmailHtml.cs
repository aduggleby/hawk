// <file>
// <summary>
// HTML building blocks for alert emails (single alerts and the grouped summary).
//
// Why: email clients ignore stylesheets and most modern CSS, so the markup is table based with inline styles and hex colours,
// no external resources. The per-monitor block built here is stored on the alert and embedded unchanged in a summary.
// </summary>
// </file>

using System.Globalization;
using System.Net;
using System.Text;

namespace Hawk.Web.Services.Monitoring;

/// <summary>
/// Inline-styled, email-client-safe HTML helpers shared by <see cref="MonitorExecutor"/> and <see cref="AlertDigestSender"/>.
/// </summary>
internal static class AlertEmailHtml
{
    public const string Failed = "#d03a2a";
    public const string StillFailing = "#9a6700";
    public const string Recovered = "#1c8a68";
    public const string Muted = "#5c6773";
    public const string Line = "#d3d8de";
    public const string Text = "#141a21";
    public const string Font = "Arial,Helvetica,sans-serif";
    public const string Mono = "Menlo,Consolas,monospace";

    /// <summary>Event colour of an alert kind (<see cref="MonitorAlertKind"/> name).</summary>
    public static string ColorFor(string kind) => kind switch
    {
        nameof(MonitorAlertKind.Recovered) => Recovered,
        nameof(MonitorAlertKind.FailureReminder) => StillFailing,
        _ => Failed,
    };

    /// <summary>Formats a time as <c>2026-10-29 12:56:14 UTC</c>.</summary>
    public static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>One label/value line of a block. A null value is skipped.</summary>
    public readonly record struct Row(string Label, string? Value, bool Monospace = false, string? Color = null);

    /// <summary>
    /// A per-monitor block: monitor name and heading in the event colour, a label/value table and optional extra HTML (failure log).
    /// </summary>
    public static string Block(string monitorName, string heading, string color, IEnumerable<Row> rows, string extraHtml = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border-collapse:collapse;font-family:{Font};color:{Text};">""");
        sb.AppendLine($"""<tr><td style="padding:14px 0 4px;border-top:1px solid {Line};"><span style="font-size:16px;font-weight:bold;">{WebUtility.HtmlEncode(monitorName)}</span> <span style="font-size:12px;font-weight:bold;color:{color};margin-left:8px;">{WebUtility.HtmlEncode(heading)}</span></td></tr>""");
        sb.AppendLine("""<tr><td style="padding:0 0 10px;"><table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;font-size:13px;">""");
        foreach (var row in rows)
        {
            if (row.Value is null)
                continue;

            var font = row.Monospace ? $"font-family:{Mono};font-size:12px;" : "";
            var tint = row.Color is null ? "" : $"color:{row.Color};";
            sb.AppendLine($"""<tr><td style="padding:3px 16px 3px 0;color:{Muted};white-space:nowrap;vertical-align:top;">{WebUtility.HtmlEncode(row.Label)}</td><td style="padding:3px 0;{font}{tint}">{WebUtility.HtmlEncode(row.Value)}</td></tr>""");
        }

        sb.AppendLine("</table>");
        sb.AppendLine(extraHtml);
        sb.AppendLine("</td></tr></table>");
        return sb.ToString();
    }

    /// <summary>
    /// The outer email document: a 600 px white card with an accent bar and a header, then the body.
    /// <paramref name="headerHtml"/> and <paramref name="bodyHtml"/> are inserted as is; callers encode any user values.
    /// </summary>
    public static string Document(string accent, string headerHtml, string bodyHtml, string? footer = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("""<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>""");
        sb.AppendLine("""<body style="margin:0;padding:0;background:#e9ecef;">""");
        sb.AppendLine("""<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#e9ecef;"><tr><td align="center" style="padding:16px 12px 32px;">""");
        sb.AppendLine($"""<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="600" style="width:600px;max-width:100%;background:#ffffff;border:1px solid {Line};font-family:{Font};color:{Text};">""");
        sb.AppendLine($"""<tr><td style="padding:20px 24px 16px;border-top:4px solid {accent};">{headerHtml}</td></tr>""");
        sb.AppendLine(bodyHtml);
        if (footer is not null)
            sb.AppendLine($"""<tr><td style="padding:16px 24px 20px;font-size:12px;color:{Muted};border-top:1px solid {Line};">{WebUtility.HtmlEncode(footer)}</td></tr>""");
        sb.AppendLine("</table></td></tr></table></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// A single alert as a full email: the per-monitor <paramref name="block"/> in the same card as the summary.
    /// </summary>
    public static string Single(string kind, string block) =>
        Document(
            ColorFor(kind),
            $"""<div style="font-size:11px;letter-spacing:0.08em;text-transform:uppercase;color:{Muted};">Hawk &middot; alert</div>""",
            $"""<tr><td style="padding:0 24px 12px;">{block}</td></tr>""");

    /// <summary>
    /// The failure log: an optional note and one line per entry (time, plus status when known).
    /// </summary>
    public static string FailureLog(string? note, IEnumerable<(DateTimeOffset At, string? Status)> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""<div style="font-size:11px;color:{Muted};text-transform:uppercase;letter-spacing:0.05em;margin-top:10px;">Failure log</div>""");
        if (note is not null)
            sb.AppendLine($"""<div style="font-size:12px;color:{Muted};margin-top:4px;">{WebUtility.HtmlEncode(note)}</div>""");

        sb.AppendLine($"""<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;font-family:{Mono};font-size:12px;margin-top:4px;">""");
        foreach (var (at, status) in entries)
        {
            var statusCell = status is null ? "" : $"""<td style="padding:2px 0;color:{Failed};">{WebUtility.HtmlEncode(status)}</td>""";
            sb.AppendLine($"""<tr><td style="padding:2px 16px 2px 0;">{FormatUtc(at)}</td>{statusCell}</tr>""");
        }

        sb.AppendLine("</table>");
        return sb.ToString();
    }
}
